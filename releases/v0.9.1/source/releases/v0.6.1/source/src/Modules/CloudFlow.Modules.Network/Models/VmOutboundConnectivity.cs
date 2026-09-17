namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// VM 的有效出站方式。
///
/// 顺序即微软文档给出的优先级（azure/load-balancer/load-balancer-outbound-connections，
/// 原文 "listed in order of priority when multiple methods are used"）：
/// NAT Gateway > Instance-level Public IP > LB outbound rules > LB 隐式 > Default outbound access。
///
/// UDR 不在这张 SNAT 方法表里 —— 它是**路由重定向**：0.0.0.0/0 指向 NVA 时，流量根本不出网卡直连，
/// 因此它排在实例级公网 IP 与 LB 之前，但仍低于 NAT Gateway（微软明确写 NAT Gateway 压制 Azure Firewall）。
/// 这个排序在界面上会连同"其他被压制的候选"一起展示，不做静默取舍。
/// </summary>
public enum OutboundConnectivityType
{
    /// <summary>
    /// 读取失败或信号不足 —— **不能下结论**。
    /// 与 <see cref="None"/> 的区别是本类型的语义核心：Unknown 是"不知道"，None 是"确定没有"。
    /// </summary>
    Unknown,

    NatGateway,

    InstancePublicIp,

    LoadBalancerOutboundRule,

    /// <summary>LB 有公网 frontend 但未配置 outbound rule（微软优先级第 4 档，隐式）。</summary>
    LoadBalancerImplicit,

    /// <summary>UDR 把出站导向 Azure Firewall / 第三方 NVA。</summary>
    UserDefinedRoute,

    /// <summary>
    /// 默认出站：由微软持有并分配的隐式公网 IP。
    /// **该地址无法通过 ARM Resource API 获得**，因此 <see cref="VmOutboundConnectivity.OutboundIp"/>
    /// 必为 null 且 <see cref="VmOutboundConnectivity.DiscoverableByArm"/> 为 false ——
    /// 但它确实**存在**，<see cref="VmOutboundConnectivity.HasPublicEgress"/> 仍为 true。
    /// </summary>
    DefaultOutboundAccess,

    /// <summary>确定没有公网出口：私有子网且无显式方法，或 UDR 的 NextHopType=None（黑洞）。</summary>
    None,

    /// <summary>
    /// 多块网卡的出站方式互不相同。**只在 VM 聚合层出现**，单块网卡的解析结果不会是本值。
    /// </summary>
    Mixed
}

/// <summary>
/// 一个被发现的出站候选。**即使最终未生效也保留**，让用户看到"这里还配了别的出站方式"。
/// </summary>
public sealed record OutboundCandidate
{
    public required OutboundConnectivityType Type { get; init; }

    /// <summary>出口 IP；无法获得时为 null（此时看 <see cref="DiscoverableByArm"/>）。</summary>
    public string? Ip { get; init; }

    /// <summary>出口 CIDR 列表（Public IP Prefix、NAT Gateway 挂多个地址时会有多项）。</summary>
    public IReadOnlyList<string> Cidrs { get; init; } = [];

    /// <summary>判定依据的资源 ID（NAT Gateway / LB / Route Table / NIC / Subnet / Public IP）。</summary>
    public string? ResourceId { get; init; }

    /// <summary>人读的依据描述，如"子网 snet-app 关联的 NAT Gateway nat-1"。</summary>
    public string? Source { get; init; }

    /// <summary>该候选是否真的参与出站（false = 被更高优先级压制）。</summary>
    public bool IsEffective { get; init; }

    /// <summary>这一跳读取失败了吗。失败时 <see cref="Ip"/> 为 null **不代表"没有出口"**。</summary>
    public bool Failed { get; init; }

    /// <summary>失败原因或补充说明。</summary>
    public string? Reason { get; init; }

    /// <summary>该候选的出口 IP 是否可通过 ARM 获得。默认出站与未解析的 NVA 为 false。</summary>
    public bool DiscoverableByArm { get; init; } = true;
}

/// <summary>
/// 单块网卡（或 VM 聚合）的出站解析结论。
///
/// <see cref="DiscoverableByArm"/> 与 <see cref="HasPublicEgress"/> 是**两个正交维度**，
/// 这是本模型存在的理由：把"出口 IP 查不到"误读成"没有公网访问"，会导致用户以为端口已经收紧。
/// </summary>
public sealed record VmOutboundConnectivity
{
    public required OutboundConnectivityType Type { get; init; }

    /// <summary>出口 IP。为 null 时必须结合 <see cref="DiscoverableByArm"/> 解读。</summary>
    public string? OutboundIp { get; init; }

    /// <summary>出口 CIDR（多个地址 / Public IP Prefix 时）。</summary>
    public IReadOnlyList<string> OutboundCidrs { get; init; } = [];

    /// <summary>出口 IP 是否可通过 ARM Resource API 获得。false ⇒ 出口 IP 是"确定未知"。</summary>
    public bool DiscoverableByArm { get; init; } = true;

    /// <summary>是否存在公网出口。与 <see cref="DiscoverableByArm"/> 正交，两者都为 false 才等于"没有公网出口"。</summary>
    public bool HasPublicEgress { get; init; }

    /// <summary>判定依据的资源 ID。</summary>
    public string? EvidenceResourceId { get; init; }

    /// <summary>中文说明，直接上 UI（含"读不到 ≠ 没有"这类提示）。</summary>
    public required string Explanation { get; init; }
}

/// <summary>单块网卡的出站解析明细（多网卡时逐块展示）。</summary>
public sealed record NicOutboundDetail
{
    public required string NicId { get; init; }

    public required string NicName { get; init; }

    public bool IsPrimary { get; init; }

    public string? SubnetId { get; init; }

    public string? SubnetName { get; init; }

    /// <summary>该网卡所在子网的 0.0.0.0/0 路由是否指向 NVA/Firewall（多网卡常跨子网，需分别标注）。</summary>
    public bool RoutedToVirtualAppliance { get; init; }

    public required VmOutboundConnectivity Result { get; init; }
}

/// <summary>
/// 一台 VM 的出站连通性总览（设计文档 §20 网络）。
/// <see cref="Effective"/> 是 VM 级结论，<see cref="Nics"/> 是逐网卡明细，<see cref="Candidates"/> 含被压制的候选。
/// </summary>
public sealed record VmOutboundOverview
{
    public required VmOutboundConnectivity Effective { get; init; }

    public required IReadOnlyList<NicOutboundDetail> Nics { get; init; }

    /// <summary>全部被发现的候选（含被优先级压制的），按优先级排列。</summary>
    public required IReadOnlyList<OutboundCandidate> Candidates { get; init; }

    /// <summary>多块网卡的出站方式是否互不相同。</summary>
    public bool NicsDiffer { get; init; }
}
