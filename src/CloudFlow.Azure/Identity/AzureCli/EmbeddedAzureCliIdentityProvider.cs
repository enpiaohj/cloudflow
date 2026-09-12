using System.Text.Json;
using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// Personal Microsoft Account 身份 Provider（P0 Spike 步骤 5，规范 §三/§五/§七）。
/// 通过 CloudFlow 托管的 Azure CLI 私有 Runtime 完成登录、订阅发现与 Token 获取：
/// - 每个账户一个独立 Profile（AZURE_CONFIG_DIR 进程级隔离）；
/// - Token 只在内存中流转，绝不落盘/写日志（CLI 自身的 Token Cache 留在 Profile 内，CloudFlow 不读取）；
/// - CLI 仅用于身份链路，资源操作禁止走本类（规范 §二）。
/// </summary>
public sealed class EmbeddedAzureCliIdentityProvider : ICloudIdentityProvider
{
    public const string AccountIdPrefix = "azurecli:";

    /// <summary>login / get-access-token 的默认超时（含用户在浏览器完成登录的时间）。</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(5);

    private readonly IAzureCliProcessRunner _runner;
    private readonly IAzureCliProfileManager _profiles;
    private readonly string _azCmdPath;

    public EmbeddedAzureCliIdentityProvider(
        IAzureCliProcessRunner runner,
        IAzureCliProfileManager profiles,
        string azCmdPath)
    {
        _runner = runner;
        _profiles = profiles;
        _azCmdPath = azCmdPath;
    }

    public AuthenticationProviderType Type => AuthenticationProviderType.EmbeddedAzureCli;

    public async Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        var profileId = _profiles.CreateProfile();
        var profilePath = _profiles.GetProfilePath(profileId);

        AzureCliResult result;
        try
        {
            result = await _runner.RunAsync(new AzureCliInvocation
            {
                ExecutablePath = _azCmdPath,
                Arguments = ["login", "--output", "json"],
                ConfigDirectory = profilePath,
                Timeout = AuthTimeout
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _profiles.DeleteProfile(profileId);
            throw;
        }

        if (!result.Succeeded)
        {
            _profiles.DeleteProfile(profileId);
            throw new AzureCliException(
                $"个人账户登录失败（退出码 {result.ExitCode}）：{AzureCliOutputRedactor.Redact(result.StandardError)}",
                result.ExitCode);
        }

        var loginRows = ParseJsonArray(result.StandardOutput);
        var username = loginRows
            .Select(row => GetJsonString(row, "user", "name"))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value))
            ?? throw new AzureCliException("个人账户登录结果中未找到用户名，已回滚该 Profile。");

        return new CloudAccount
        {
            AccountId = AccountIdPrefix + profileId,
            Username = username,
            DisplayName = username.Split('@')[0],
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = profileId
        };
    }

    public async Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
        CloudAccount account,
        CancellationToken cancellationToken = default)
    {
        var profilePath = ResolveProfilePath(account);

        var result = await _runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = _azCmdPath,
            Arguments = ["account", "list", "--all", "--output", "json"],
            ConfigDirectory = profilePath
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new AzureCliException(
                $"订阅发现失败（退出码 {result.ExitCode}）：{AzureCliOutputRedactor.Redact(result.StandardError)}",
                result.ExitCode);
        }

        var subscriptions = ParseJsonArray(result.StandardOutput)
            .Select(row => new SubscriptionProfile
            {
                SubscriptionId = GetJsonString(row, "id") ?? "",
                DisplayName = GetJsonString(row, "name") ?? "",
                TenantId = GetJsonString(row, "tenantId") ?? "",
                State = GetJsonString(row, "state") ?? "Enabled",
                IsSelected = true
            })
            .ToList();

        return subscriptions;
    }

    public async Task<CloudAccessToken> GetCredentialAsync(
        CloudCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.ProviderType != Type)
        {
            throw new ArgumentException(
                $"凭据上下文的 ProviderType 为 {context.ProviderType}，与 {Type} 不匹配。", nameof(context));
        }

        var profilePath = _profiles.GetProfilePath(
            context.ProviderProfileId
            ?? throw new ArgumentException("EmbeddedAzureCli 凭据上下文必须携带 ProviderProfileId。", nameof(context)));

        return new CloudAccessToken
        {
            AcquireAsync = async (scopes, token) =>
            {
                // scope 形如 https://management.azure.com/.default → CLI 的 --resource 去掉 /.default 后缀
                var resource = (scopes.FirstOrDefault() ?? "https://management.azure.com/.default")
                    .Replace("/.default", string.Empty, StringComparison.OrdinalIgnoreCase);

                var result = await _runner.RunAsync(new AzureCliInvocation
                {
                    ExecutablePath = _azCmdPath,
                    Arguments = ["account", "get-access-token",
                        "--resource", resource, "--tenant", context.TenantId, "--output", "json"],
                    ConfigDirectory = profilePath,
                    Timeout = AuthTimeout
                }, token).ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    throw new AzureCliException(
                        $"获取访问令牌失败（退出码 {result.ExitCode}）：{AzureCliOutputRedactor.Redact(result.StandardError)}",
                        result.ExitCode);
                }

                using var document = JsonDocument.Parse(result.StandardOutput);
                return document.RootElement.GetProperty("accessToken").GetString()
                    ?? throw new AzureCliException("访问令牌响应中 accessToken 为空。");
            }
        };
    }

    public async Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        // 仅登出（清除 CLI Token），不删除 Profile，避免影响后续重新登录（规范 §二十一 单账户登出隔离）
        await _profiles.LogoutAsync(ResolveProfileId(account), cancellationToken).ConfigureAwait(false);
    }

    private string ResolveProfilePath(CloudAccount account)
    {
        return _profiles.GetProfilePath(ResolveProfileId(account));
    }

    private static string ResolveProfileId(CloudAccount account)
    {
        if (!account.AccountId.StartsWith(AccountIdPrefix, StringComparison.Ordinal) ||
            account.ProviderProfileId is null)
        {
            throw new ArgumentException(
                $"账户 {account.AccountId} 不是 Embedded Azure CLI 身份。", nameof(account));
        }

        return account.ProviderProfileId;
    }

    private static JsonElement[] ParseJsonArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }

    private static string? GetJsonString(JsonElement row, params string[] path)
    {
        var current = row;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }
}
