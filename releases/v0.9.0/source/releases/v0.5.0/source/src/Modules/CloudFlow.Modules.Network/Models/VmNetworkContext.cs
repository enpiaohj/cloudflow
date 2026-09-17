namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// VM 网络上下文（设计文档 §20 / 概念图 2 左侧 Network Summary + Inbound Rules）。
/// 以 VM 为中心聚合 NIC / VNet / Subnet / NSG，用户不需要跨 Resource Provider 查找。
/// </summary>
public sealed record VmNetworkContext
{
    public required string VmResourceId { get; init; }

    public required string NicName { get; init; }

    public required string VnetName { get; init; }

    public required string SubnetName { get; init; }

    public required string SubnetCidr { get; init; }

    public required string NsgName { get; init; }

    /// <summary>网卡级 NSG 的 Resource ID（未关联时为 null）。新增 NIC 规则时的目标。</summary>
    public string? NicNsgId { get; init; }

    /// <summary>子网级 NSG 的 Resource ID（未关联时为 null）。</summary>
    public string? SubnetNsgId { get; init; }

    /// <summary>子网 Resource ID（共享 NSG 影响面统计用，§25）。</summary>
    public string? SubnetId { get; init; }

    /// <summary>主 NIC 的公网 IP（未关联 Public IP 时为 null）。</summary>
    public string? PublicIp { get; init; }

    /// <summary>公网 IP 资源名（概念图 2：Public IP 行显示 "ip (资源名)"，便于在门户中定位）。</summary>
    public string? PublicIpName { get; init; }

    /// <summary>
    /// 主网卡公网 IP 的完整限定域名（如 <c>appscloud.koreacentral.cloudapp.azure.com</c>）。
    /// 只有配置了 DNS 名称标签的公网 IP 才有；未配置时为 null，此为该行**不显示**的依据，
    /// 不是"没读到"。
    /// </summary>
    public string? PublicIpFqdn { get; init; }

    /// <summary>主 NIC 的专用 IP。</summary>
    public string? PrivateIp { get; init; }

    /// <summary>true = 规则位于 Subnet 级 NSG，可能影响同子网多台 VM（§25 Shared NSG）。</summary>
    public bool IsSharedSubnetNsg { get; init; }

    /// <summary>安全状态汇总（如 "Protected"）。</summary>
    public string SecurityState { get; init; } = "Protected";

    /// <summary>
    /// 入站规则。用户填的对端在这里的 <see cref="NsgSecurityRule.Source"/> 一侧。
    /// </summary>
    public required IReadOnlyList<NsgSecurityRule> InboundRules { get; init; }

    /// <summary>
    /// 出站规则。与 <see cref="InboundRules"/> 同源同表（同一次 NSG 读取），只按方向分流 ——
    /// 分开是因为两侧"用户填的那个地址"分别落在目标侧与来源侧，
    /// 合成一张表会出现一半的列对一半的行没意义。
    /// </summary>
    public IReadOnlyList<NsgSecurityRule> OutboundRules { get; init; } = [];

    /// <summary>
    /// 全部网卡上的入站公网 IP。单网卡时与 <see cref="PublicIp"/> 一致；
    /// 多网卡时补上 <see cref="PublicIp"/> 覆盖不到的那些 —— 过去只读第一块网卡，其余地址直接丢失。
    /// </summary>
    public IReadOnlyList<InboundPublicIp> InboundPublicIps { get; init; } = [];

    /// <summary>
    /// 出站连通性解析结果（NAT Gateway / 实例级公网 IP / LB 出站规则 / UDR / 默认出站）。
    /// null = 未解析（例如测试替身未提供），**不等于"没有公网出口"** —— 后者是
    /// <see cref="OutboundConnectivityType.None"/>，两者不可混用。
    /// </summary>
    public VmOutboundOverview? Outbound { get; init; }
}
