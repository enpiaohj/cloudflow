namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Demo 公网 IP（203.0.113.10 是 RFC 5737 文档专用地址段，本来就不可路由）。
///
/// 只允许未登录的 Demo 模式使用。登录真实账户后若仍拿它当"我的当前 IP"，
/// 界面就会把用户真实的公网 IP 显示成一个不存在的文档地址。
/// </summary>
public sealed class MockCurrentIpProvider : ICurrentIpProvider
{
    public const string DemoIp = "203.0.113.10";

    public Task<string?> GetCurrentIpAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(DemoIp);

    public Task<string?> GetCurrentIpCidrAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>($"{DemoIp}/32");
}
