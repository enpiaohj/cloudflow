namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// My Current IP（设计文档 §24）。
/// CloudFlow 自动识别用户当前公网 IP，用于快速建立 "TCP 3389 / Source = Current IP" 规则。
///
/// 返回 null 表示**无法确定**当前公网 IP。此时调用方必须隐藏该选项并让用户手填，
/// 绝不能替换成一个占位地址：用户会以为"只放行我自己"，实际规则放行的是别人，
/// 结果是自己连不上、还误以为端口是安全的。
/// </summary>
public interface ICurrentIpProvider
{
    Task<string?> GetCurrentIpAsync(CancellationToken ct = default);

    /// <summary>带 /32 的 CIDR 形式，如 "203.0.113.10/32"；无法确定时为 null。</summary>
    Task<string?> GetCurrentIpCidrAsync(CancellationToken ct = default);
}
