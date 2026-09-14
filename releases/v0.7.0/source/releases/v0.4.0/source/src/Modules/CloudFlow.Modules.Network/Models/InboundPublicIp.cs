namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 一块网卡上的**入站**公网 IP。
///
/// 命名纪律（设计决策 §13）：入站侧一律 <c>Inbound...</c>，出站侧一律 <c>Outbound...</c>，
/// 不出现裸 <c>PublicIp</c> 的新用法。原因很实际 —— 同一个公网 IP 在两侧含义不同：
/// 挂在一块网卡上的公网 IP 既是入站地址，也**可能**是实例级出站地址；
/// 但出站还有 NAT Gateway / LB 出站规则 / UDR / 默认出站四条与它无关的路径。
/// 把两者混成一个字段，就会重现"把入站 IP 当成出口 IP"这个错误。
///
/// <see cref="VmNetworkContext.PublicIp"/> 仍然只描述**主网卡**的入站公网 IP（保持既有语义不变），
/// 本类型补上多网卡场景下被丢掉的其他地址。
/// </summary>
public sealed record InboundPublicIp
{
    public required string Address { get; init; }

    /// <summary>公网 IP 资源名（概念图 2：Public IP 行显示 "ip (资源名)"，便于在门户中定位）。</summary>
    public string? Name { get; init; }

    public string? ResourceId { get; init; }

    /// <summary>
    /// 公网 IP 资源的完整限定域名（如 <c>appscloud.koreacentral.cloudapp.azure.com</c>）。
    /// 没配 DNS 名称标签时为 null —— 这是常态，不代表读取失败。
    /// </summary>
    public string? Fqdn { get; init; }

    public required string NicId { get; init; }

    public required string NicName { get; init; }

    /// <summary>该公网 IP 是否挂在主网卡上。</summary>
    public bool IsPrimaryNic { get; init; }
}
