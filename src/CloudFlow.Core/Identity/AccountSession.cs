namespace CloudFlow.Core.Identity;

/// <summary>
/// 一次已登录会话：账户上下文 + 可见 Tenant 列表 + Token 获取委托。
/// Token 的实际获取由平台层（CloudFlow.Azure / MSAL）注入，Core 不依赖 MSAL。
/// </summary>
public sealed class AccountSession
{
    public required CloudAccount Account { get; init; }

    public IReadOnlyList<TenantProfile> Tenants { get; init; } = [];

    /// <summary>按 scopes 获取 Access Token（平台层实现，通常内部走 AcquireTokenSilent → Interactive 回退）。</summary>
    public required Func<IEnumerable<string>, CancellationToken, Task<string>> AccessTokenProvider { get; init; }

    public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken ct = default)
        => AccessTokenProvider(scopes, ct);
}
