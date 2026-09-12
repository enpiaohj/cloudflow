using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using TenantProfile = CloudFlow.Core.Identity.TenantProfile;

namespace CloudFlow.Azure.Auth;

/// <summary>
/// 基于 MSAL.NET 的多账号会话管理（设计文档 §69/§70）。
/// - Token 获取：AcquireTokenSilent 优先，失败回退 AcquireTokenInteractive
/// - 多账号：Token Cache 中的账户（GetAccountsAsync）
/// - WAM Broker：待 App Registration 配置对应 Redirect URI 后启用（WithBroker）
/// </summary>
public sealed class MsalAccountSessionManager : IAccountSessionManager
{
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
        var scopes = new[] { _config.ManagementScope, "user.read" };

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

    public async Task<AccountSession?> GetActiveSessionAsync(CancellationToken ct = default)
    {
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        var account = accounts.OrderByDescending(a => a.LastUsedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (account is null)
        {
            return null;
        }

        var tenants = await GetTenantsAsync(account, ct).ConfigureAwait(false);
        return new AccountSession
        {
            Account = account,
            Tenants = tenants,
            AccessTokenProvider = (scopes, token) => AcquireTokenAsync(account.AccountId, scopes, token)
        };
    }

    public async Task<IReadOnlyList<TenantProfile>> GetTenantsAsync(CloudAccount account, CancellationToken ct = default)
    {
        // Tenant 发现：从 ARM /subscriptions 返回结果中提取（每个 Subscription 携带 tenantId）。
        // 完整实现随后续迭代接入 ARM Subscriptions API；当前返回 Home Tenant 占位。
        var tenants = new List<TenantProfile>();
        var homeTenantId = account.HomeTenantId;
        if (homeTenantId is not null)
        {
            tenants.Add(new TenantProfile
            {
                TenantId = homeTenantId,
                AccountId = account.AccountId,
                DisplayName = homeTenantId,
                DefaultDomain = account.Username[(account.Username.IndexOf('@') + 1)..]
            });
        }
        return tenants;
    }

    /// <summary>Silent 优先的 Token 获取（设计文档 §69）。</summary>
    private async Task<string> AcquireTokenAsync(string accountId, IEnumerable<string> scopes, CancellationToken ct)
    {
        var client = await GetClientAsync().ConfigureAwait(false);
        var scopeList = scopes.ToArray();
        var account = await client.GetAccountAsync(accountId).ConfigureAwait(false);

        if (account is not null)
        {
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
                _logger.LogInformation("Silent token acquisition failed ({ErrorCode}), falling back to interactive",
                    ex.ErrorCode);
            }
        }

        var interactive = await client
            .AcquireTokenInteractive(scopeList)
            .WithAccount(account)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);
        return interactive.AccessToken;
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
                .WithDefaultRedirectUri()
                .WithLogging((level, message, _) =>
                    _logger.Log(MapLogLevel(level), "[MSAL] {Message}", message));

            // WAM Broker：需要 App Registration 配置
            // ms-appx-web://microsoft.aad.brokerplugin/{clientId} Redirect URI 后启用
            // builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows));

            _client = builder.Build();
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static CloudAccount ToCloudAccount(IAccount account) => new()
    {
        AccountId = account.HomeAccountId?.Identifier ?? account.Username,
        Username = account.Username,
        DisplayName = string.IsNullOrWhiteSpace(account.Username) ? "" :
            account.Username.Split('@')[0],
        HomeTenantId = account.HomeAccountId?.TenantId,
        LastUsedAt = DateTimeOffset.Now
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
}
