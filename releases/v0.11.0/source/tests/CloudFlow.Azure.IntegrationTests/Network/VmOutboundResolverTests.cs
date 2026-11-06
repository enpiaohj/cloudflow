using CloudFlow.Azure.Network;
using CloudFlow.Modules.Network.Models;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

/// <summary>
/// VM 出站连通性解析。
///
/// 这组断言守的是本仓最容易犯、后果也最重的一类错误：把"读不到"当成"没有"。
/// 出口 IP 拿不到时如果报成"无公网出口"，用户会以为端口已经收紧，
/// 而实际情况可能是默认出站（地址在微软手里，确实拿不到）——
/// 两者的处置方式完全相反，所以模型用 DiscoverableByArm 与 HasPublicEgress
/// 两个正交维度把它们分开，这里逐个把真值表钉住。
///
/// 全部用例都是纯内存构造：VmOutboundResolver 不碰 Azure，这也是它被设计成纯函数的原因。
/// </summary>
public class VmOutboundResolverTests
{
    private const string Sub = "11111111-1111-1111-1111-111111111111";
    private const string VmId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string NicId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string SubnetId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/virtualNetworks/vnet1/subnets/snet1";
    private const string NatId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/natGateways/nat1";
    private const string RtId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/routeTables/rt1";
    private const string LbId = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/loadBalancers/lb1";
    private const string PoolId = $"{LbId}/backendAddressPools/pool1";
    private const string FrontendId = $"{LbId}/frontendIPConfigurations/fe1";

    private static string PipId(string name) =>
        $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.Network/publicIPAddresses/{name}";

    // ---------- 优先级：微软文档 "listed in order of priority when multiple methods are used" ----------

    [Fact]
    public void NAT网关压制实例级公网IP()
    {
        var graph = Graph(
            nic: Nic(instancePip: "52.141.44.28"),
            subnet: Subnet(natGatewayId: NatId),
            natGateways: [Nat(NatId, "nat1", pipIds: [PipId("nat-pip")])],
            publicIps: [Pip("nat-pip", "20.9.9.9"), Pip("vm-pip", "52.141.44.28")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.NatGateway, overview.Effective.Type);
        Assert.Equal("20.9.9.9", overview.Effective.OutboundIp);
        Assert.True(overview.Effective.DiscoverableByArm);
        Assert.True(overview.Effective.HasPublicEgress);

        // 被压制的那一个必须留在候选里 —— 静默丢掉会让用户以为这里没配过实例级公网 IP
        var suppressed = Assert.Single(overview.Candidates, c => c.Type == OutboundConnectivityType.InstancePublicIp);
        Assert.False(suppressed.IsEffective);
        Assert.Equal("52.141.44.28", suppressed.Ip);
    }

    [Fact]
    public void NAT网关压制负载均衡器出站规则()
    {
        var graph = Graph(
            nic: Nic(backendPoolIds: [PoolId]),
            subnet: Subnet(natGatewayId: NatId),
            natGateways: [Nat(NatId, "nat1", pipIds: [PipId("nat-pip")])],
            loadBalancers: [Lb(outboundRules: [new OutboundLbOutboundRule
            {
                Name = "outbound-rule-1",
                BackendAddressPoolId = PoolId,
                FrontendIpConfigRefs = [FrontendId],
            }])],
            publicIps: [Pip("nat-pip", "20.9.9.9"), Pip("lb-pip", "52.1.1.1")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.NatGateway, overview.Effective.Type);
        Assert.Equal("20.9.9.9", overview.Effective.OutboundIp);
        Assert.Contains(overview.Candidates,
            c => c.Type == OutboundConnectivityType.LoadBalancerOutboundRule && !c.IsEffective);
    }

    [Fact]
    public void NAT网关压制UDR指向的AzureFirewall()
    {
        // 微软原文：NAT Gateway "takes precedence over other outbound connectivity methods,
        // including a load balancer, instance-level public IP addresses, and Azure Firewall"。
        var nvaIp = "10.0.1.4";
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(natGatewayId: NatId, routeTableId: RtId),
            natGateways: [Nat(NatId, "nat1", pipIds: [PipId("nat-pip")])],
            routeTables: [Rt(routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: nvaIp)])],
            publicIps: [Pip("nat-pip", "20.9.9.9")],
            nvaResults: new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase)
            {
                [nvaIp] = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    OutboundIp = "13.13.13.13",
                    DiscoverableByArm = true,
                    HasPublicEgress = true,
                    Explanation = "测试用：防火墙出口",
                },
            });

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.NatGateway, overview.Effective.Type);
        Assert.Contains(overview.Candidates,
            c => c.Type == OutboundConnectivityType.UserDefinedRoute && !c.IsEffective);
    }

    [Fact]
    public void 实例级公网IP压制负载均衡器出站规则()
    {
        // 微软原文：实例级公网 IP "takes precedence over the others, except for NAT Gateway"。
        var graph = Graph(
            nic: Nic(instancePip: "52.141.44.28", backendPoolIds: [PoolId]),
            loadBalancers: [Lb(outboundRules: [new OutboundLbOutboundRule
            {
                Name = "outbound-rule-1",
                BackendAddressPoolId = PoolId,
                FrontendIpConfigRefs = [FrontendId],
            }])],
            publicIps: [Pip("vm-pip", "52.141.44.28"), Pip("lb-pip", "52.1.1.1")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.InstancePublicIp, overview.Effective.Type);
        Assert.Equal("52.141.44.28", overview.Effective.OutboundIp);
    }

    [Fact]
    public void 负载均衡器出站规则生效时取规则绑定的前端公网IP()
    {
        var graph = Graph(
            nic: Nic(backendPoolIds: [PoolId]),
            loadBalancers: [Lb(outboundRules: [new OutboundLbOutboundRule
            {
                Name = "outbound-rule-1",
                BackendAddressPoolId = PoolId,
                FrontendIpConfigRefs = [FrontendId],
            }])],
            publicIps: [Pip("lb-pip", "52.1.1.1")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.LoadBalancerOutboundRule, overview.Effective.Type);
        Assert.Equal("52.1.1.1", overview.Effective.OutboundIp);
        Assert.True(overview.Effective.HasPublicEgress);
    }

    [Fact]
    public void 进了后端池但没有出站规则时按隐式出站处理()
    {
        // 微软文档：虚拟机在后端池中且前端有公网 IP 时，"the load balancer frontend(s) are
        // still used for outbound, but this is done implicitly"。
        var graph = Graph(
            nic: Nic(backendPoolIds: [PoolId]),
            loadBalancers: [Lb(outboundRules: [])],
            publicIps: [Pip("lb-pip", "52.1.1.1")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.LoadBalancerImplicit, overview.Effective.Type);
        Assert.Equal("52.1.1.1", overview.Effective.OutboundIp);
    }

    [Fact]
    public void 没有任何显式方式时报默认出站且出口IP不可通过ARM获得()
    {
        var overview = VmOutboundResolver.Resolve(Graph(nic: Nic(), subnet: Subnet()));

        Assert.Equal(OutboundConnectivityType.DefaultOutboundAccess, overview.Effective.Type);

        // 真值表的关键一行：出口确实存在，但地址拿不到。两个维度必须同时成立。
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.False(overview.Effective.DiscoverableByArm);
        Assert.Null(overview.Effective.OutboundIp);
    }

    [Fact]
    public void 私有子网且无显式方式时报无公网出口()
    {
        var overview = VmOutboundResolver.Resolve(Graph(
            nic: Nic(defaultOutboundEnabled: false),
            subnet: Subnet(defaultOutboundAccess: false)));

        Assert.Equal(OutboundConnectivityType.None, overview.Effective.Type);
        Assert.False(overview.Effective.HasPublicEgress);
        Assert.True(overview.Effective.DiscoverableByArm);
    }

    [Fact]
    public void 默认路由黑洞时报无公网出口()
    {
        var overview = VmOutboundResolver.Resolve(Graph(
            nic: Nic(),
            subnet: Subnet(routeTableId: RtId),
            routeTables: [Rt(routes: [Route(nextHopType: "None")])]));

        Assert.Equal(OutboundConnectivityType.None, overview.Effective.Type);
        Assert.False(overview.Effective.HasPublicEgress);
    }

    // ---------- UDR → NVA ----------

    [Fact]
    public void UDR指向的NVA解析成功时给出NVA的出口IP()
    {
        var nvaIp = "10.0.1.4";
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(routeTableId: RtId),
            routeTables: [Rt(routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: nvaIp)])],
            nvaResults: new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase)
            {
                [nvaIp] = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    OutboundIp = "13.13.13.13",
                    DiscoverableByArm = true,
                    HasPublicEgress = true,
                    Explanation = "测试用：经防火墙出口",
                },
            });

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.UserDefinedRoute, overview.Effective.Type);
        Assert.Equal("13.13.13.13", overview.Effective.OutboundIp);
        Assert.True(overview.Effective.DiscoverableByArm);
    }

    [Fact]
    public void UDR指向无法解析的NVA时报告跳地址并声明ARM无法获取()
    {
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(routeTableId: RtId),
            routeTables: [Rt(routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: "10.0.1.9")])]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.UserDefinedRoute, overview.Effective.Type);

        // 出口由第三方设备决定，CloudFlow 解析不到最终公网 IP —— 但出口确实存在
        Assert.False(overview.Effective.DiscoverableByArm);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.Contains("10.0.1.9", overview.Effective.Explanation);
    }

    [Fact]
    public void 两条子网路由指向不同NVA时结论互不串用()
    {
        // NvaResults 以**下一跳 IP** 为键而不是 VM ID：两块网卡指向不同 NVA 时，
        // 用 VM 级单值会把 A 子网的结论套到 B 子网头上。
        var graph = Graph(
            nics:
            [
                Nic(instancePip: null, nicId: $"{NicId}-a", subnetId: $"{SubnetId}-a"),
                Nic(instancePip: null, nicId: $"{NicId}-b", subnetId: $"{SubnetId}-b"),
            ],
            subnets:
            [
                Subnet(subnetId: $"{SubnetId}-a", name: "snet-a", routeTableId: $"{RtId}-a"),
                Subnet(subnetId: $"{SubnetId}-b", name: "snet-b", routeTableId: $"{RtId}-b"),
            ],
            routeTables:
            [
                Rt(rtId: $"{RtId}-a", routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: "10.0.1.4")]),
                Rt(rtId: $"{RtId}-b", routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: "10.0.2.4")]),
            ],
            nvaResults: new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase)
            {
                ["10.0.1.4"] = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    OutboundIp = "1.1.1.1",
                    DiscoverableByArm = true,
                    HasPublicEgress = true,
                    Explanation = "A 子网的 NVA",
                },
                ["10.0.2.4"] = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    OutboundIp = "2.2.2.2",
                    DiscoverableByArm = true,
                    HasPublicEgress = true,
                    Explanation = "B 子网的 NVA",
                },
            });

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(2, overview.Nics.Count);
        var a = Assert.Single(overview.Nics, n => n.SubnetName == "snet-a");
        var b = Assert.Single(overview.Nics, n => n.SubnetName == "snet-b");
        Assert.Equal("1.1.1.1", a.Result.OutboundIp);
        Assert.Equal("2.2.2.2", b.Result.OutboundIp);
    }

    [Fact]
    public void UDR指向的NVA自身也是UDR时只取一层不递归()
    {
        // 递归限深 1 层（ArmOutboundGraphReader.MaxNvaDepth）是调用方的约束，
        // 这里钉住解析器侧的行为：NvaResults 里放什么就报什么，绝不再往下追。
        // 若解析器会自行递归，这个用例会因为环而挂死。
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(routeTableId: RtId),
            routeTables: [Rt(routes: [Route(nextHopType: "VirtualAppliance", nextHopIp: "10.0.1.4")])],
            nvaResults: new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase)
            {
                // 这个"结论"本身又是一个指向别处的 UDR —— 构成自环
                ["10.0.1.4"] = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    DiscoverableByArm = false,
                    HasPublicEgress = true,
                    Explanation = "该 NVA 的出口由另一台设备决定",
                },
            });

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.UserDefinedRoute, overview.Effective.Type);
        Assert.False(overview.Effective.DiscoverableByArm);
        Assert.Null(overview.Effective.OutboundIp);
    }

    // ---------- 多网卡 ----------

    [Fact]
    public void 多网卡出站方式不一致时逐块列出且标记不一致()
    {
        var graph = Graph(
            nics:
            [
                Nic(instancePip: "52.141.44.28", nicId: $"{NicId}-1", isPrimary: true),
                Nic(instancePip: null, nicId: $"{NicId}-2", isPrimary: false, subnetId: $"{SubnetId}-2",
                    defaultOutboundEnabled: false),
            ],
            subnets:
            [
                Subnet(),
                Subnet(subnetId: $"{SubnetId}-2", name: "snet-2", defaultOutboundAccess: false),
            ],
            publicIps: [Pip("vm-pip", "52.141.44.28")]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.True(overview.NicsDiffer);
        Assert.Equal(OutboundConnectivityType.Mixed, overview.Effective.Type);
        Assert.Equal(2, overview.Nics.Count);
        Assert.Equal(OutboundConnectivityType.InstancePublicIp,
            Assert.Single(overview.Nics, n => n.IsPrimary).Result.Type);
        Assert.Equal(OutboundConnectivityType.None,
            Assert.Single(overview.Nics, n => !n.IsPrimary).Result.Type);
    }

    [Fact]
    public void 一块网卡挂了公网IP另一块是私有子网时不整体报默认出站()
    {
        var graph = Graph(
            nics:
            [
                Nic(instancePip: "52.141.44.28", nicId: $"{NicId}-1", isPrimary: true),
                Nic(instancePip: null, nicId: $"{NicId}-2", isPrimary: false, subnetId: $"{SubnetId}-2",
                    defaultOutboundEnabled: false),
            ],
            subnets:
            [
                Subnet(),
                Subnet(subnetId: $"{SubnetId}-2", name: "snet-2", defaultOutboundAccess: false),
            ],
            publicIps: [Pip("vm-pip", "52.141.44.28")]);

        var overview = VmOutboundResolver.Resolve(graph);

        // 聚合层不能因为"有一块网卡没出口"就退回默认出站，也不能报成"没有"
        Assert.NotEqual(OutboundConnectivityType.DefaultOutboundAccess, overview.Effective.Type);
        Assert.NotEqual(OutboundConnectivityType.None, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
    }

    [Fact]
    public void 一块网卡都没读到时不报无公网出口()
    {
        var overview = VmOutboundResolver.Resolve(Graph(nics: []));

        Assert.Equal(OutboundConnectivityType.Unknown, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.False(overview.Effective.DiscoverableByArm);
        Assert.Contains("不等于没有公网出口", overview.Effective.Explanation);
    }

    // ---------- 失败语义：读不到 ≠ 没有 ----------

    [Fact]
    public void 读取失败时默认出站结论升级为未知且仍报存在公网出口()
    {
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(),
            readFailures: ["未能读取负载均衡器 lb1"]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.Unknown, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.False(overview.Effective.DiscoverableByArm);
        Assert.Contains("未能读取负载均衡器 lb1", overview.Effective.Explanation);
    }

    [Fact]
    public void 读取失败时私有子网的None结论也必须升级为未知()
    {
        // 这条是本轮修掉的一个真实缺口：旧的失败守卫用优先级序号判定，
        // 而 None 在优先级表里排在最前，于是"私有子网 + 某跳读取失败"会直接报 None ——
        // 用户看到"无公网出口"就以为端口已经收紧，而读取失败的那一跳可能正藏着
        // 一条 LB 出站规则。否定结论必须由完整的读取来支撑。
        var graph = Graph(
            nic: Nic(defaultOutboundEnabled: false),
            subnet: Subnet(defaultOutboundAccess: false),
            readFailures: ["未能读取子网关联的路由表 rt1"]);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.NotEqual(OutboundConnectivityType.None, overview.Effective.Type);
        Assert.Equal(OutboundConnectivityType.Unknown, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.False(overview.Effective.DiscoverableByArm);
    }

    [Fact]
    public void 读取失败不影响已经读到的显式出站方式()
    {
        var graph = Graph(
            nic: Nic(instancePip: "52.141.44.28"),
            subnet: Subnet(),
            publicIps: [Pip("vm-pip", "52.141.44.28")],
            readFailures: ["未能读取负载均衡器 lb1"]);

        var overview = VmOutboundResolver.Resolve(graph);

        // 已经读到实据的结论不该被别处的失败拉低 —— 降级只会丢信息，不会更安全
        Assert.Equal(OutboundConnectivityType.InstancePublicIp, overview.Effective.Type);
        Assert.Equal("52.141.44.28", overview.Effective.OutboundIp);
    }

    [Fact]
    public void 子网关联了路由表但读不到路由表时保守报UDR而非无出口()
    {
        var graph = Graph(
            nic: Nic(),
            subnet: Subnet(routeTableId: RtId),
            routeTables: []);

        var overview = VmOutboundResolver.Resolve(graph);

        Assert.Equal(OutboundConnectivityType.UserDefinedRoute, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.False(overview.Effective.DiscoverableByArm);
    }

    [Fact]
    public void 网卡级与子网级默认出站标志矛盾时以网卡侧为准并如实说明()
    {
        // 微软文档：NIC 级标志只在 stop/deallocate 之后刷新，
        // 改了子网 defaultOutboundAccess 而不重启，两者就会不一致。
        var graph = Graph(
            nic: Nic(defaultOutboundEnabled: true),
            subnet: Subnet(defaultOutboundAccess: false));

        var overview = VmOutboundResolver.Resolve(graph);

        // 保守方向：宁可说"有出口"
        Assert.Equal(OutboundConnectivityType.DefaultOutboundAccess, overview.Effective.Type);
        Assert.True(overview.Effective.HasPublicEgress);
        Assert.Contains("默认出站", overview.Effective.Explanation);
    }

    // ---------- 文案：DiscoverableByArm == false 时不得渲染成「无」 ----------

    [Theory]
    [InlineData(OutboundConnectivityType.DefaultOutboundAccess)]
    [InlineData(OutboundConnectivityType.Unknown)]
    public void 出口IP不可得时文案不得说成没有公网出口(OutboundConnectivityType type)
    {
        var connectivity = new VmOutboundConnectivity
        {
            Type = type,
            DiscoverableByArm = false,
            HasPublicEgress = true,
            Explanation = "测试",
        };

        var text = OutboundText.DescribeIp(connectivity);

        Assert.Equal(OutboundText.UnknownOutboundIp, text);
        Assert.NotEqual(OutboundText.NoPublicEgress, text);
        Assert.NotEqual("—", text);
    }

    [Fact]
    public void 确定没有出口时才说没有公网出口()
    {
        var connectivity = new VmOutboundConnectivity
        {
            Type = OutboundConnectivityType.None,
            DiscoverableByArm = true,
            HasPublicEgress = false,
            Explanation = "测试",
        };

        Assert.Equal(OutboundText.NoPublicEgress, OutboundText.DescribeIp(connectivity));
    }

    [Fact]
    public void 默认出站的说明文字必须点明不代表没有公网出口()
    {
        var overview = VmOutboundResolver.Resolve(Graph(nic: Nic(), subnet: Subnet()));

        Assert.Contains("无法通过 ARM 获得", overview.Effective.Explanation);
        Assert.Contains("不代表没有公网出口", overview.Effective.Explanation);
    }

    // ---------- 构造辅助 ----------

    private static OutboundGraph Graph(
        OutboundNic? nic = null,
        IReadOnlyList<OutboundNic>? nics = null,
        OutboundSubnet? subnet = null,
        IReadOnlyList<OutboundSubnet>? subnets = null,
        IReadOnlyList<OutboundNatGateway>? natGateways = null,
        IReadOnlyList<OutboundLoadBalancer>? loadBalancers = null,
        IReadOnlyList<OutboundRouteTable>? routeTables = null,
        IReadOnlyList<OutboundPublicIp>? publicIps = null,
        IReadOnlyList<OutboundPublicIpPrefix>? publicIpPrefixes = null,
        IReadOnlyList<string>? readFailures = null,
        Dictionary<string, VmOutboundConnectivity>? nvaResults = null)
        => new()
        {
            VmResourceId = VmId,
            Nics = nics ?? [nic ?? Nic()],
            Subnets = Index(subnets ?? [subnet ?? Subnet()], s => s.SubnetId),
            NatGateways = Index(natGateways ?? [], g => g.ResourceId),
            LoadBalancers = Index(loadBalancers ?? [], l => l.ResourceId),
            RouteTables = Index(routeTables ?? [], r => r.ResourceId),
            PublicIps = Index(publicIps ?? [], p => p.ResourceId),
            PublicIpPrefixes = Index(publicIpPrefixes ?? [], p => p.ResourceId),
            ReadFailures = readFailures ?? [],
            NvaResults = nvaResults
                ?? new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase),
        };

    private static IReadOnlyDictionary<string, T> Index<T>(IReadOnlyList<T> items, Func<T, string> keyOf)
        => items.ToDictionary(keyOf, StringComparer.OrdinalIgnoreCase);

    private static OutboundNic Nic(
        string? nicId = null,
        bool isPrimary = true,
        string? instancePip = null,
        IReadOnlyList<string>? backendPoolIds = null,
        string? subnetId = null,
        bool? defaultOutboundEnabled = null)
        => new()
        {
            NicId = nicId ?? NicId,
            NicName = "nic1",
            IsPrimary = isPrimary,
            DefaultOutboundConnectivityEnabled = defaultOutboundEnabled,
            IpConfigurations =
            [
                new OutboundIpConfig
                {
                    IsPrimary = true,
                    SubnetId = subnetId ?? SubnetId,
                    PublicIpId = instancePip is null ? null : PipId("vm-pip"),
                    LoadBalancerBackendPoolIds = backendPoolIds ?? [],
                }
            ],
        };

    private static OutboundSubnet Subnet(
        string? subnetId = null,
        string name = "snet1",
        string? natGatewayId = null,
        string? routeTableId = null,
        bool? defaultOutboundAccess = null)
        => new()
        {
            SubnetId = subnetId ?? SubnetId,
            Name = name,
            NatGatewayId = natGatewayId,
            RouteTableId = routeTableId,
            DefaultOutboundAccess = defaultOutboundAccess,
        };

    private static OutboundNatGateway Nat(string id, string name, IReadOnlyList<string> pipIds) => new()
    {
        ResourceId = id,
        Name = name,
        PublicIpIds = pipIds,
        PublicIpPrefixIds = [],
    };

    private static OutboundLoadBalancer Lb(IReadOnlyList<OutboundLbOutboundRule> outboundRules) => new()
    {
        ResourceId = LbId,
        Name = "lb1",
        IsStandardSku = true,
        BackendAddressPoolIds = [PoolId],
        OutboundRules = outboundRules,
        // 键同时放了 frontend 的资源 ID 与名字：出站规则引用 ID，读 frontend 列表拿到对象
        FrontendPublicIpIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FrontendId] = PipId("lb-pip"),
            ["fe1"] = PipId("lb-pip"),
        },
    };

    private static OutboundRouteTable Rt(
        IReadOnlyList<OutboundRoute> routes,
        string? rtId = null) => new()
    {
        ResourceId = rtId ?? RtId,
        Name = "rt1",
        Routes = routes,
    };

    private static OutboundRoute Route(string nextHopType, string? nextHopIp = null) => new()
    {
        Name = "default",
        AddressPrefix = "0.0.0.0/0",
        NextHopType = nextHopType,
        NextHopIpAddress = nextHopIp,
    };

    private static OutboundPublicIp Pip(string name, string address) => new()
    {
        ResourceId = PipId(name),
        IpAddress = address,
        SkuName = "Standard",
    };
}
