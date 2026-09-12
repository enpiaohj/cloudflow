using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using TenantProfile = CloudFlow.Core.Identity.TenantProfile;

namespace CloudFlow.Azure.Auth;

/// <summary>
/// 基于 MSAL.NET 的多账号会话管理（设计文档 §69/§70）。
/// - Token Cache 持久化：%LOCALAPPDATA%\CloudFlow\msal-token-cache.bin（DPAPI 加密），
///   重启应用后 TryRestoreSessionAsync 静默恢复登录，无需再次交互登录
/// - Token 获取：数据链路仅 AcquireTokenSilent（Refresh Token 自动续期）；
///   静默失败抛 <see cref="ReauthenticationRequiredException"/>，绝不弹浏览器
/// - 多账号：Token Cache 中的账户（GetAccountsAsync），P1 单活动账户取第一个
/// - WAM Broker：待 App Registration 配置对应 Redirect URI 后启用（WithBroker）
/// </summary>
public sealed class MsalAccountSessionManager : IAccountSessionManager
{
    /// <summary>与 CloudFlow.Data 的 CloudFlowPaths 同一数据目录约定。</summary>
    private const string CacheDirectorySubPath = "CloudFlow";
    private const string CacheFileName = "msal-token-cache.bin";

    private static readonly HttpClient Http = new();

    private readonly MsalAuthConfig _config;
    private readonly ILogger<MsalAccountSessionManager> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private IPublicClientApplication? _client;

    public MsalAccountSessionManager(MsalAuthConfig config, ILogger<MsalAccountSessionManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => _config.IsConfigured;

    public async Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var client = await GetClientAsync().ConfigureAwait(false);
        var accounts = await client.GetAccountsAsync().ConfigureAwait(false);
        return [.. accounts.Select(ToCloudAccount)];
    }

    public async Task<CloudAccount> AddAccountAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new NotConfiguredException(
                "Azure App Registration 未配置。请复制 appsettings.example.json 为 appsettings.json 并填入 ClientId。");
        }

        var client = await GetClientAsync().ConfigureAwait(false);

        // 仅请求 ARM scope：CloudFlow 不调用 Microsoft Graph（user.read 徒增同意环节）
        var scopes = new[] { _config.ManagementScope };

        var result = await client
            .AcquireTokenInteractive(scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Added account {Username} (tenant {Tenant})",
            result.Account.Username, result.Account.HomeAccountId?.TenantId);

        return ToCloudAccount(result.Account);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        var client = await GetClientAsync().ConfigureAwait(false);
        var account = await client.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account is not null)
        {
            await client.RemoveAsync(account).ConfigureAwait(false);
            _logger.LogInformation("Removed account {AccountId} from token cache", accountId);
        }
    }

    public async Task<AccountSession?> GetSessionAsync(string accountId, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var client = await GetClientAsync().ConfigureAwait(false);
        var account = await client.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        return await CreateSessionAsync(ToCloudAccount(account), ct).ConfigureAwait(false);
    }

    public async Task<AccountSession?> TryRestoreSessionAsync(
        string? preferredAccountId = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        var candidates = accounts.OrderByDescending(account =>
            string.Equals(account.AccountId, preferredAccountId, StringComparison.Ordinal));

        foreach (var account in candidates)
        {
            try
            {
                // 静默试探：确认持久化 Cache 中的 Token 仍可用（含 Refresh Token 续期）。
                // 失败时继续尝试其他缓存账户，绝不触发交互式登录。
                await AcquireTokenAsync(account.AccountId, [_config.ManagementScope], ct).ConfigureAwait(false);

                var session = await CreateSessionAsync(account, ct).ConfigureAwait(false);
                _logger.LogInformation("Restored session for {Username} from persisted token cache", account.Username);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex,
                    "Session restore failed for {Username} ({ErrorType})", account.Username, ex.GetType().Name);
            }
        }

        return null;
    }

    private async Task<AccountSession> CreateSessionAsync(CloudAccount account, CancellationToken ct)
    {
        var tenants = await GetTenantsAsync(account, ct).ConfigureAwait(false);
        return new AccountSession
        {
            Account = account,
            Tenants = tenants,
            AccessTokenProvider = (scopes, token) => AcquireTokenAsync(account.AccountId, scopes, token)
        };
    }

    /// <summary>
    /// Tenant 发现（设计文档 §35/§72）：ARM /tenants 列出账户可访问的全部租户。
    /// 查询失败（未登录/网络）时回退 Home Tenant 占位，不影响主流程。
    /// </summary>
    public async Task<IReadOnlyList<TenantProfile>> GetTenantsAsync(CloudAccount account, CancellationToken ct = default)
    {
        var fallback = HomeTenantFallback(account);
        if (!IsConfigured)
        {
            return fallback;
        }

        try
        {
            var token = await AcquireTokenAsync(account.AccountId, [_config.ManagementScope], ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://management.azure.com/tenants?api-version=2022-12-01");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CloudFlowException(CloudFlowErrorCode.AzureError,
                    $"Tenant 发现失败（{(int)response.StatusCode}）：{Truncate(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var result = new List<TenantProfile>();
            foreach (var tenant in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var tenantId = tenant.GetProperty("tenantId").GetString();
                if (string.IsNullOrEmpty(tenantId))
                {
                    continue;
                }

                result.Add(new TenantProfile
                {
                    TenantId = tenantId,
                    AccountId = account.AccountId,
                    DisplayName = tenant.TryGetProperty("displayName", out var n) && !string.IsNullOrEmpty(n.GetString())
                        ? n.GetString()!
                        : tenantId,
                    DefaultDomain = GetDomains(tenant),
                    LastRefreshedAt = DateTimeOffset.Now
                });
            }

            return result.Count > 0
                ? [.. result.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)]
                : fallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ARM /tenants 查询失败，回退 Home Tenant 占位");
            return fallback;
        }
    }

    /// <summary>
    /// Silent Token 获取（设计文档 §69）：Refresh Token 自动续期；
    /// 失败抛 <see cref="ReauthenticationRequiredException"/>，由 UI 引导用户重新登录。
    /// </summary>
    private async Task<string> AcquireTokenAsync(string accountId, IEnumerable<string> scopes, CancellationToken ct)
    {
        var client = await GetClientAsync().ConfigureAwait(false);
        var scopeList = scopes.ToArray();
        var account = await client.GetAccountAsync(accountId).ConfigureAwait(false);

        if (account is null)
        {
            throw new ReauthenticationRequiredException(
                "登录会话不存在（可能已退出登录），请到「设置 → 使用 Microsoft 登录」重新登录。");
        }

        try
        {
            var silent = await client
                .AcquireTokenSilent(scopeList, account)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.LogInformation("Silent token acquisition failed ({ErrorCode}); reauthentication required",
                ex.ErrorCode);
            throw new ReauthenticationRequiredException(
                "登录会话已失效，请到「设置 → 使用 Microsoft 登录」重新登录。", ex);
        }
        catch (MsalException ex)
        {
            // 网络 / 服务端错误：如实上抛，不吞错（全局规范：不隐藏 Error）
            throw new CloudFlowException(CloudFlowErrorCode.AzureError,
                $"获取访问令牌失败（{ex.ErrorCode}）：{ex.Message}", ex);
        }
    }

    private async Task<IPublicClientApplication> GetClientAsync()
    {
        if (_client is not null)
        {
            return _client;
        }

        await _clientLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var builder = PublicClientApplicationBuilder
                .Create(_config.ClientId)
                .WithAuthority($"{_config.Instance}{_config.TenantId}")
                // 显式 loopback redirect：唯一确定的系统浏览器登录路径。
                // WithDefaultRedirectUri() 在 net8.0-windows TFM 下会误选需要 WebView2 的路径
                .WithRedirectUri(_config.RedirectUri)
                .WithLogging((level, message, _) =>
                    _logger.Log(MapLogLevel(level), "[MSAL] {Message}", message));

            var client = builder.Build();
            await RegisterPersistentTokenCacheAsync(client).ConfigureAwait(false);

            _client = client;
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Token Cache 落盘（%LOCALAPPDATA%\CloudFlow\msal-token-cache.bin，Windows DPAPI 加密）。
    /// 注册失败（权限等）降级为内存 Cache 并告警 —— 不阻断登录，代价是重启后需重新登录。
    /// </summary>
    private async Task RegisterPersistentTokenCacheAsync(IPublicClientApplication client)
    {
        try
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CacheDirectorySubPath);
            var properties = new StorageCreationPropertiesBuilder(CacheFileName, cacheDirectory)
                .Build();

            var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
            helper.RegisterCache(client.UserTokenCache);
            _logger.LogInformation("MSAL token cache persisted at {Path}",
                Path.Combine(cacheDirectory, CacheFileName));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MSAL token cache 持久化注册失败，降级为内存 Cache（重启后需重新登录）");
        }
    }

    private static IReadOnlyList<TenantProfile> HomeTenantFallback(CloudAccount account)
    {
        if (account.HomeTenantId is null)
        {
            return [];
        }

        var at = account.Username.IndexOf('@');
        return
        [
            new TenantProfile
            {
                TenantId = account.HomeTenantId,
                AccountId = account.AccountId,
                DisplayName = account.HomeTenantId,
                DefaultDomain = at >= 0 ? account.Username[(at + 1)..] : ""
            }
        ];
    }

    /// <summary>/tenants additionalProperties.domains（可选字段，取第一个域名）。</summary>
    private static string GetDomains(JsonElement tenant)
    {
        try
        {
            if (tenant.TryGetProperty("additionalProperties", out var ap) &&
                ap.TryGetProperty("domains", out var domains) &&
                domains.ValueKind == JsonValueKind.Array)
            {
                foreach (var domain in domains.EnumerateArray())
                {
                    var value = domain.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 可选字段解析失败可忽略
        }
        return "";
    }

    private static CloudAccount ToCloudAccount(IAccount account) => new()
    {
        AccountId = account.HomeAccountId?.Identifier ?? account.Username,
        Username = account.Username,
        DisplayName = string.IsNullOrWhiteSpace(account.Username) ? "" :
            account.Username.Split('@')[0],
        ProviderType = AuthenticationProviderType.EntraMsal,
        HomeTenantId = account.HomeAccountId?.TenantId
    };

    /// <summary>MSAL LogLevel → Microsoft.Extensions.Logging.LogLevel。</summary>
    private static Microsoft.Extensions.Logging.LogLevel MapLogLevel(Microsoft.Identity.Client.LogLevel msalLevel) =>
        msalLevel switch
        {
            Microsoft.Identity.Client.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            Microsoft.Identity.Client.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            Microsoft.Identity.Client.LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            Microsoft.Identity.Client.LogLevel.Verbose => Microsoft.Extensions.Logging.LogLevel.Debug,
            _ => Microsoft.Extensions.Logging.LogLevel.Trace
        };

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
