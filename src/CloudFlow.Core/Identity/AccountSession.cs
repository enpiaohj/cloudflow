namespace CloudFlow.Core.Identity;

/// <summary>
/// 一次已登录会话：账户上下文 + 可见 Tenant 列表 + Token 获取委托。
/// Token 的实际获取由平台层（CloudFlow.Azure / MSAL）注入，Core 不依赖 MSAL。
/// </summary>
public sealed class AccountSession
{
    public required CloudAccount Account { get; init; }

    public IReadOnlyList<TenantProfile> Tenants { get; init; } = [];

    /// <summary>
    /// 按 scopes 获取 Access Token（平台层实现，AcquireTokenSilent，会用 Refresh Token 自动续期）。
    /// 静默失败（如 Refresh Token 被撤销）时抛出要求重新登录的错误，绝不弹出交互式登录窗。
    /// </summary>
    public required Func<IEnumerable<string>, CancellationToken, Task<string>> AccessTokenProvider { get; init; }

    public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken ct = default)
        => AccessTokenProvider(scopes, ct);
}
