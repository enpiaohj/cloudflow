using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Identity;

/// <summary>
/// 账户目录：把 MSAL 缓存的 Entra 工作/学校账户与个人 Microsoft 账户
/// （Embedded Azure CLI 身份）合并为统一账户列表，并按 ProviderType 解析
/// 应使用的身份 Provider。上层 UI 只依赖本服务，不自行判断账户类型，
/// 也不直接接触 MSAL / Azure CLI。
///
/// 账户来源：
/// - Entra：MSAL Token Cache 是唯一权威来源（本目录不复制其状态）；
/// - Personal：CloudFlow 自有的非敏感元数据注册表（CLI Profile 只保存 Token）。
/// </summary>
public sealed class CloudAccountDirectory
{
    private readonly IReadOnlyDictionary<AuthenticationProviderType, ICloudIdentityProvider> _providers;
    private readonly IAccountSessionManager _sessions;
    private readonly IPersonalAccountRegistry _personalAccounts;

    public CloudAccountDirectory(
        IEnumerable<ICloudIdentityProvider> providers,
        IAccountSessionManager sessions,
        IPersonalAccountRegistry personalAccounts)
    {
        _providers = providers.ToDictionary(provider => provider.Type);
        _sessions = sessions;
        _personalAccounts = personalAccounts;
    }

    /// <summary>全部已登录账户：Entra 缓存账户 + 已保存的个人账户（按 AccountId 去重）。</summary>
    public async Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var accounts = new List<CloudAccount>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var account in await _sessions.GetAccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(account.AccountId))
            {
                accounts.Add(account);
            }
        }

        foreach (var account in _personalAccounts.Load())
        {
            if (seen.Add(account.AccountId))
            {
                accounts.Add(account);
            }
        }

        return accounts;
    }

    /// <summary>按账户的 ProviderType 解析身份 Provider；未注册时明确拒绝，绝不静默换用其他账户。</summary>
    public ICloudIdentityProvider ResolveProvider(CloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return _providers.TryGetValue(account.ProviderType, out var provider)
            ? provider
            : throw new NotSupportedException(
                $"账户 {account.AccountId} 的身份 Provider 类型 {account.ProviderType} 未注册。");
    }

    /// <summary>
    /// 登录个人 Microsoft 账户（嵌入式 Azure CLI 设备码流）。
    /// <paramref name="onSignInMessage"/> 实时收到设备码与验证网址等登录指引，供 UI 展示。
    /// 成功后仅登记非敏感元数据，Token 始终留在 Provider 内部。
    /// </summary>
    public async Task<CloudAccount> SignInPersonalAccountAsync(
        Action<string> onSignInMessage,
        CancellationToken cancellationToken = default)
    {
        if (FindProvider(AuthenticationProviderType.EmbeddedAzureCli) is not IInteractiveSignInProgress interactive)
        {
            throw new NotSupportedException(
                "个人 Microsoft 账户登录在当前版本不可用（未注册对应身份 Provider）。");
        }

        var account = await interactive.SignInAsync(onSignInMessage, cancellationToken).ConfigureAwait(false);
        _personalAccounts.Save(account);
        return account;
    }

    /// <summary>移除账户：由对应 Provider 清除本机凭据状态，并清理个人账户注册表。</summary>
    public async Task RemoveAccountAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        await ResolveProvider(account).SignOutAsync(account, cancellationToken).ConfigureAwait(false);
        ForgetAccount(account.AccountId);
    }

    /// <summary>按 ProviderType 查找已注册的身份 Provider；未注册时返回 null。</summary>
    public ICloudIdentityProvider? FindProvider(AuthenticationProviderType type) =>
        _providers.TryGetValue(type, out var provider) ? provider : null;

    /// <summary>登录成功后登记个人账户（仅非敏感元数据），供重启后恢复。</summary>
    public void RememberPersonalAccount(CloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.ProviderType != AuthenticationProviderType.EmbeddedAzureCli)
        {
            throw new ArgumentException(
                $"仅个人 Microsoft 账户（EmbeddedAzureCli）需要本机注册；收到 {account.ProviderType}。",
                nameof(account));
        }

        _personalAccounts.Save(account);
    }

    /// <summary>移除账户时清理本机注册；Entra 账户由 MSAL 缓存负责，本目录不复制其状态。</summary>
    public void ForgetAccount(string accountId)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            _personalAccounts.Remove(accountId);
        }
    }
}
