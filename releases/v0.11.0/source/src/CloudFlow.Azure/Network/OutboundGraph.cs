using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Azure.Network;

/// <summary>
/// 出站解析所需的完整资源图快照：VM → NIC → IP Configuration → Subnet → NAT Gateway / Route Table / LB / Public IP。
///
/// 刻意与 ARM 读取分离 —— <see cref="VmOutboundResolver"/> 只吃本结构，因此优先级判定、多网卡聚合、
/// 失败语义这些最容易出错的逻辑可以用纯内存数据直接测，不需要真 Azure。
/// 所有资源按 **Resource ID 去重**后传入：多网卡常共用同一个子网，不去重会重复查询。
/// </summary>
public sealed record OutboundGraph
{
    public required string VmResourceId { get; init; }

    public required IReadOnlyList<OutboundNic> Nics { get; init; }

    public required IReadOnlyDictionary<string, OutboundSubnet> Subnets { get; init; }

    public required IReadOnlyDictionary<string, OutboundNatGateway> NatGateways { get; init; }

    public required IReadOnlyDictionary<string, OutboundLoadBalancer> LoadBalancers { get; init; }

    public required IReadOnlyDictionary<string, OutboundRouteTable> RouteTables { get; init; }

    public required IReadOnlyDictionary<string, OutboundPublicIp> PublicIps { get; init; }

    public required IReadOnlyDictionary<string, OutboundPublicIpPrefix> PublicIpPrefixes { get; init; }

    /// <summary>
    /// 读取失败的跳（人读描述）。**非空时不允许下"没有公网出口"的结论** ——
    /// 读不到某一跳只说明不知道，不说明不存在。
    /// </summary>
    public IReadOnlyList<string> ReadFailures { get; init; } = [];

    /// <summary>
    /// UDR 指向第三方 NVA 时，递归解析该 NVA VM 得到的结论（限深 1 层，由调用方防环后传入）。
    /// **键是路由的 NextHopIpAddress**，不是 VM ID —— 不同子网可以指向不同 NVA，
    /// 用地址索引才不会把 A 子网的结论套到 B 子网头上。
    /// 某地址查不到键 = 该 NVA 未能解析（跨订阅、无权限、或本来就不是 NIC 私网 IP）。
    /// </summary>
    public IReadOnlyDictionary<string, VmOutboundConnectivity> NvaResults { get; init; }
        = new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一块网卡及其全部 IP 配置（不再只取 [0]）。</summary>
public sealed record OutboundNic
{
    public required string NicId { get; init; }

    public required string NicName { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>
    /// NIC 级默认出站标志。true = Azure 已给这块网卡分配了 default outbound IP。
    /// null = 未读到。
    /// **注意**：微软文档明确该标志在修改子网 defaultOutboundAccess 后必须 stop/deallocate 虚拟机才刷新，
    /// 因此它与子网侧属性可能不一致 —— 两者都要看，矛盾时如实说明。
    /// </summary>
    public bool? DefaultOutboundConnectivityEnabled { get; init; }

    public required IReadOnlyList<OutboundIpConfig> IpConfigurations { get; init; }
}

/// <summary>一个 NIC IP 配置上与本解析相关的三条引用。</summary>
public sealed record OutboundIpConfig
{
    public bool IsPrimary { get; init; }

    public string? SubnetId { get; init; }

    /// <summary>NIC 上的公网 IP 资源 ID。同时是**入站**公网 IP 与**实例级出站**的候选。</summary>
    public string? PublicIpId { get; init; }

    /// <summary>该 IP 配置所属的 LB 后端池 ID 列表（进池 ≠ 有 outbound rule，还要看 LB 侧）。</summary>
    public IReadOnlyList<string> LoadBalancerBackendPoolIds { get; init; } = [];
}

/// <summary>子网侧与出站相关的三个属性。</summary>
public sealed record OutboundSubnet
{
    public required string SubnetId { get; init; }

    public required string Name { get; init; }

    /// <summary>关联的 NAT Gateway ID（未关联为 null）。</summary>
    public string? NatGatewayId { get; init; }

    /// <summary>关联的 Route Table ID（未关联为 null）。</summary>
    public string? RouteTableId { get; init; }

    /// <summary>
    /// 子网侧 defaultOutboundAccess。null = 未读到。
    /// Azure 语义：null（属性缺省）视为允许默认出站；false 表示私有子网。
    /// </summary>
    public bool? DefaultOutboundAccess { get; init; }
}

public sealed record OutboundNatGateway
{
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> PublicIpIds { get; init; }

    /// <summary>Public IP Prefix ID 列表（NAT Gateway 挂前缀时出口是一段地址，不是一个）。</summary>
    public required IReadOnlyList<string> PublicIpPrefixIds { get; init; }
}

public sealed record OutboundLoadBalancer
{
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Standard SKU 才参与出站；Basic SKU 的出站连接已被微软退役。
    /// null = 未读到 SKU，不得据此否定出站。
    /// </summary>
    public bool? IsStandardSku { get; init; }

    /// <summary>该负载均衡器拥有的后端池 ID，用于判断本网卡是否真的进了它的池。</summary>
    public required IReadOnlyList<string> BackendAddressPoolIds { get; init; }

    public required IReadOnlyList<OutboundLbOutboundRule> OutboundRules { get; init; }

    /// <summary>
    /// frontend IP 配置 → 该 frontend 的公网 IP 资源 ID。
    /// 键同时放了 frontend 的**资源 ID 与名字**：出站规则里引用的是 ID，
    /// 而读 frontend 列表拿到的是对象，两个来源的标识方式不同，两个键都放才不用猜。
    /// </summary>
    public required IReadOnlyDictionary<string, string> FrontendPublicIpIds { get; init; }
}

public sealed record OutboundLbOutboundRule
{
    public required string Name { get; init; }

    /// <summary>该出站规则作用的后端池 ID。要与 NIC 的 IP 配置所属池匹配才算命中。</summary>
    public string? BackendAddressPoolId { get; init; }

    /// <summary>该出站规则引用的 frontend IP 配置（资源 ID，用于查 <see cref="OutboundLoadBalancer.FrontendPublicIpIds"/>）。</summary>
    public required IReadOnlyList<string> FrontendIpConfigRefs { get; init; }
}

public sealed record OutboundRouteTable
{
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<OutboundRoute> Routes { get; init; }
}

public sealed record OutboundRoute
{
    public required string Name { get; init; }

    public required string AddressPrefix { get; init; }

    /// <summary>
    /// 下一跳类型。用字符串而不是 SDK 的可扩展枚举 —— 本仓在 SecurityRuleProtocol 上吃过
    /// "可扩展枚举不能用于 switch 模式"的亏，字符串比较更稳且便于纯逻辑测试。
    /// 取值：Internet / VirtualAppliance / None / VirtualNetworkGateway / VnetLocal / VirtualNetworkServiceEndpoint。
    /// </summary>
    public required string NextHopType { get; init; }

    public string? NextHopIpAddress { get; init; }
}

public sealed record OutboundPublicIp
{
    public required string ResourceId { get; init; }

    public string? IpAddress { get; init; }

    public string? SkuName { get; init; }

    /// <summary>该公网 IP 属于某个 Public IP Prefix 时，指回前缀资源。</summary>
    public string? PrefixId { get; init; }

    /// <summary>
    /// 公网 IP 资源上配置的 DNS 名称标签（<c>dnsSettings.domainNameLabel</c>）。
    /// 未配置 DNS 名称标签时为 null —— 这是常态，不是读取失败。
    /// </summary>
    public string? DnsLabel { get; init; }

    /// <summary>
    /// 完整限定域名（<c>dnsSettings.fqdn</c>，如 <c>vm-01.koreacentral.cloudapp.azure.com</c>）。
    /// 与 <see cref="DnsLabel"/> 的区别是它带区域后缀，可直接用于连接。
    /// </summary>
    public string? Fqdn { get; init; }
}

public sealed record OutboundPublicIpPrefix
{
    public required string ResourceId { get; init; }

    /// <summary>前缀的 CIDR，如 20.0.0.0/30。</summary>
    public string? IpPrefix { get; init; }

    public int? PrefixLength { get; init; }
}
