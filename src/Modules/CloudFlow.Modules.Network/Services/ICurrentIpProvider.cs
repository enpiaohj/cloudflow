namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// My Current IP（设计文档 §24）。
/// CloudFlow 自动识别用户当前公网 IP，用于快速建立 "TCP 3389 / Source = Current IP" 规则。
/// </summary>
public interface ICurrentIpProvider
{
    Task<string> GetCurrentIpAsync(CancellationToken ct = default);

    /// <summary>带 /32 的 CIDR 形式，如 "203.0.113.10/32"。</summary>
    Task<string> GetCurrentIpCidrAsync(CancellationToken ct = default);
}
