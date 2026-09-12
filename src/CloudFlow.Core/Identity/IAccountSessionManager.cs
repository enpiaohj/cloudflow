namespace CloudFlow.Core.Identity;

/// <summary>
/// 多账号会话管理平台接口（设计文档 §70）。
/// Resource Module 不允许直接管理 MSAL。
/// </summary>
public interface IAccountSessionManager
{
    /// <summary>App Registration 是否已配置（未配置时 Demo/Mock 模式）。</summary>
    bool IsConfigured { get; }

    /// <summary>Token Cache 中的可用账户（MSAL GetAccountsAsync）。</summary>
    Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken ct = default);

    /// <summary>交互式添加账户（AcquireTokenInteractive）。</summary>
    Task<CloudAccount> AddAccountAsync(CancellationToken ct = default);

    /// <summary>只清除 CloudFlow Token Cache 中对应账户，不影响 Microsoft 账户本身（§6）。</summary>
    Task RemoveAccountAsync(string accountId, CancellationToken ct = default);

    /// <summary>获取当前活动会话（未登录时为 null）。</summary>
    Task<AccountSession?> GetActiveSessionAsync(CancellationToken ct = default);

    /// <summary>发现账户可访问的 Tenant 列表。</summary>
    Task<IReadOnlyList<TenantProfile>> GetTenantsAsync(CloudAccount account, CancellationToken ct = default);
}
