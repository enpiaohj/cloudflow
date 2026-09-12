namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Demo 公网 IP（203.0.113.10 是 RFC 5737 文档专用地址段）。
/// 真实实现通过公网 IP 探测服务获取。
/// </summary>
public sealed class MockCurrentIpProvider : ICurrentIpProvider
{
    public const string DemoIp = "203.0.113.10";

    public Task<string> GetCurrentIpAsync(CancellationToken ct = default) =>
        Task.FromResult(DemoIp);

    public Task<string> GetCurrentIpCidrAsync(CancellationToken ct = default) =>
        Task.FromResult($"{DemoIp}/32");
}
