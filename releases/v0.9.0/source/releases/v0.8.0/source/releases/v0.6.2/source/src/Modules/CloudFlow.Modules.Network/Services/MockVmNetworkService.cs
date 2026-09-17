using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Mock VM 网络服务（Demo 模式）。
/// VM-WEB01 的数据与 UI 概念图 2 一致：共享 Subnet NSG nsg-web-prod + 6 条入站规则；
/// 出站规则概念图里没有，另给了 3 条用来覆盖"出站表格里同时有 Allow 与 Deny"的渲染。
/// 规则变更由 Operation Handler 调用本类的 Mutate 方法完成（数据面），审批面在 Engine。
/// </summary>
public sealed class MockVmNetworkService : IVmNetworkService
{
    private readonly Dictionary<string, VmNetworkContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    public MockVmNetworkService()
    {
        Seed();
    }

    public Task<VmNetworkContext?> GetForVmAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var snapshot = BuildContext(vmResourceId);
            return Task.FromResult(snapshot);
        }
    }

    public Task<VmOutboundOverview?> GetOutboundAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var outbound = _contexts.TryGetValue(vmResourceId, out var ctx) ? ctx.Outbound : null;
            return Task.FromResult(outbound);
        }
    }

    /// <summary>两个方向都查 —— 与 ARM 实现的语义一致，否则出站规则的改 / 删会找不到目标。</summary>
    public Task<NsgSecurityRule?> FindRuleAsync(string vmResourceId, string ruleId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var rule = _contexts.TryGetValue(vmResourceId, out var ctx)
                ? ctx.InboundRules.Concat(ctx.OutboundRules).FirstOrDefault(r =>
                    string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                : null;
            return Task.FromResult(rule);
        }
    }

    public Task<IReadOnlyList<NsgSecurityRule>> GetInboundRulesAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<NsgSecurityRule> rules = _contexts.TryGetValue(vmResourceId, out var ctx)
                ? [.. ctx.InboundRules.OrderBy(r => r.Priority)]
                : [];
            return Task.FromResult(rules);
        }
    }

    public Task<IReadOnlyList<NsgSecurityRule>> GetOutboundRulesAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<NsgSecurityRule> rules = _contexts.TryGetValue(vmResourceId, out var ctx)
                ? [.. ctx.OutboundRules.OrderBy(r => r.Priority)]
                : [];
            return Task.FromResult(rules);
        }
    }

    // ==== 供 Operation Handler 调用的数据面操作 ====

    /// <summary>Change Port（§22）：只改端口，其他字段不动。</summary>
    public bool TryChangePort(string vmResourceId, string ruleId, int newPort)
    {
        lock (_lock)
        {
            var rule = Find(vmResourceId, ruleId);
            if (rule is null)
            {
                return false;
            }
            rule.DestinationPort = newPort;
            return true;
        }
    }

    /// <summary>Open Port（§23）：按规则自身的方向追加到对应列表。</summary>
    public NsgSecurityRule AddRule(string vmResourceId, NsgSecurityRule rule)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(vmResourceId, out var ctx))
            {
                throw new InvalidOperationException($"No network context for {vmResourceId}");
            }

            if (rule.Direction == NsgRuleDirection.Outbound)
            {
                var outbound = ctx.OutboundRules.ToList();
                outbound.Add(rule);
                _contexts[vmResourceId] = ctx with { OutboundRules = outbound };
            }
            else
            {
                var inbound = ctx.InboundRules.ToList();
                inbound.Add(rule);
                _contexts[vmResourceId] = ctx with { InboundRules = inbound };
            }

            return rule;
        }
    }

    /// <summary>Delete Rule（§21）。按 ruleId 定位，两个方向都找 —— 规则 ID 自身已含 NSG 与规则名。</summary>
    public bool TryDeleteRule(string vmResourceId, string ruleId)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(vmResourceId, out var ctx))
            {
                return false;
            }

            var inbound = ctx.InboundRules.ToList();
            var outbound = ctx.OutboundRules.ToList();
            var removed = inbound.RemoveAll(r => Matches(r, ruleId)) + outbound.RemoveAll(r => Matches(r, ruleId));
            if (removed > 0)
            {
                _contexts[vmResourceId] = ctx with { InboundRules = inbound, OutboundRules = outbound };
            }

            return removed > 0;
        }
    }

    private NsgSecurityRule? Find(string vmResourceId, string ruleId) =>
        _contexts.TryGetValue(vmResourceId, out var ctx)
            ? ctx.InboundRules.Concat(ctx.OutboundRules).FirstOrDefault(r => Matches(r, ruleId))
            : null;

    private static bool Matches(NsgSecurityRule rule, string ruleId) =>
        string.Equals(rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase);

    private VmNetworkContext? BuildContext(string vmResourceId)
    {
        return _contexts.TryGetValue(vmResourceId, out var ctx) ? ctx : null;
    }

    private void Seed()
    {
        // 与概念图 2 一致的 VM-WEB01 网络上下文
        var web01 = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/WEB01";
        var nic = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Network/networkSecurityGroups/nsg-web01-nic";
        var subnet = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Network/networkSecurityGroups/nsg-web-prod";

        var rules = new List<NsgSecurityRule>
        {
            new()
            {
                RuleId = $"{nic}/securityRules/RDP", Name = "RDP",
                Source = $"My IP ({MockCurrentIpProvider.DemoIp})", SourcePrefix = $"{MockCurrentIpProvider.DemoIp}/32",
                DestinationPort = 3389, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Nic, Priority = 300
            },
            new()
            {
                RuleId = $"{subnet}/securityRules/HTTPS", Name = "HTTPS",
                Source = "Any", SourcePrefix = "*",
                DestinationPort = 443, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Subnet, Priority = 310
            },
            new()
            {
                RuleId = $"{nic}/securityRules/WebAdmin", Name = "WebAdmin",
                Source = "Any", SourcePrefix = "*",
                DestinationPort = 18080, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Nic, Priority = 350
            },
            new()
            {
                RuleId = $"{subnet}/securityRules/SSH", Name = "SSH",
                Source = "Any", SourcePrefix = "*",
                DestinationPort = 22, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Deny, Origin = NsgRuleOrigin.Subnet, Priority = 320
            },
            new()
            {
                RuleId = $"{subnet}/securityRules/HealthProbe", Name = "HealthProbe",
                Source = "AzureLoadBalancer", SourcePrefix = "AzureLoadBalancer",
                DestinationPort = 8080, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Subnet, Priority = 330
            },
            new()
            {
                RuleId = $"{nic}/securityRules/CustomApp", Name = "CustomApp",
                Source = "10.0.2.0/24", SourcePrefix = "10.0.2.0/24",
                DestinationPort = 5000, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Nic, Priority = 340
            }
        };

        // 出站规则的对端在**目标**一侧；来源恒为 "*"（本机），与入站规则刚好相反
        var outboundRules = new List<NsgSecurityRule>
        {
            new()
            {
                RuleId = $"{subnet}/securityRules/AllowInternetOut", Name = "AllowInternetOut",
                Source = "Any", SourcePrefix = "*",
                Destination = "Internet", DestinationPrefix = "Internet",
                DestinationPort = 443, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Subnet, Priority = 4000,
                Direction = NsgRuleDirection.Outbound
            },
            new()
            {
                RuleId = $"{nic}/securityRules/AllowSqlOut", Name = "AllowSqlOut",
                Source = "Any", SourcePrefix = "*",
                Destination = "10.0.3.0/24", DestinationPrefix = "10.0.3.0/24",
                DestinationPort = 1433, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Nic, Priority = 4010,
                Direction = NsgRuleDirection.Outbound
            },
            new()
            {
                RuleId = $"{subnet}/securityRules/DenySmbOut", Name = "DenySmbOut",
                Source = "Any", SourcePrefix = "*",
                Destination = "10.0.4.0/24", DestinationPrefix = "10.0.4.0/24",
                DestinationPort = 445, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Deny, Origin = NsgRuleOrigin.Subnet, Priority = 4020,
                Direction = NsgRuleDirection.Outbound
            }
        };

        _contexts[web01] = new VmNetworkContext
        {
            VmResourceId = web01,
            NicName = "web01-nic",
            VnetName = "vnet-prod",
            SubnetName = "snet-web",
            SubnetCidr = "10.0.1.0/24",
            NsgName = "nsg-web-prod",
            NicNsgId = nic,
            SubnetNsgId = subnet,
            SubnetId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Network/virtualNetworks/vnet-prod/subnets/snet-web",
            PublicIp = "20.51.13.7",
            PublicIpName = "web01-ip",
            PrivateIp = "10.0.1.4",
            IsSharedSubnetNsg = true,
            SecurityState = "Protected",
            InboundRules = rules,
            OutboundRules = outboundRules,
            InboundPublicIps =
            [
                new InboundPublicIp
                {
                    Address = "20.51.13.7",
                    Name = "web01-ip",
                    ResourceId = PublicIpId(Sub, "rg-web", "web01-ip"),
                    NicId = NicId(Sub, "rg-web", "web01-nic"),
                    NicName = "web01-nic",
                    IsPrimaryNic = true,
                }
            ],
            Outbound = InstancePublicIpOutbound()
        };

        SeedOutboundVariants();
    }

    /// <summary>
    /// 覆盖全部出站方式各一台 —— 没有这些数据，Demo 模式下「出站连接」卡片只会在 WEB01 上有内容，
    /// 深色/浅色与各分支都没法看。
    /// </summary>
    private void SeedOutboundVariants()
    {
        Add(
            VmId(Sub, "rg-app", "APP01"), "app01-nic", "vnet-prod", "snet-app", "10.0.2.0/24", "10.0.2.8",
            NatGatewayOutbound());

        Add(
            VmId(Sub, "rg-ops", "MON01"), "mon01-nic", "vnet-ops", "snet-ops", "10.0.5.0/24", "10.0.5.20",
            LoadBalancerOutbound());

        Add(
            VmId(Sub, "rg-shared", "BASTION01"), "bastion01-nic", "vnet-shared", "snet-bastion", "10.0.0.0/24",
            "10.0.0.10", FirewallRouteOutbound());

        Add(
            VmId(Sub, "rg-dev", "DEV01"), "dev01-nic", "vnet-dev", "snet-dev", "10.0.9.0/24", "10.0.9.5",
            DefaultOutbound());

        Add(
            VmId(Sub, "rg-data", "SQL01"), "sql01-nic", "vnet-data", "snet-data", "10.0.3.0/24", "10.0.3.5",
            NoEgressOutbound());

        Add(
            VmId(Sub, "rg-legacy", "OLD-SQL"), "oldsql-nic", "vnet-legacy", "snet-legacy", "10.0.6.0/24",
            "10.0.6.15", UnresolvedRouteOutbound());

        Add(
            VmId(Sub, "rg-shared", "JUMPBOX"), "jumpbox-nic", "vnet-shared", "snet-shared", "10.0.0.0/24",
            "10.0.0.12", MixedOutbound(), inboundPublicIp: "20.51.13.50");
    }

    /// <summary>
    /// 入站公网 IP 必须**单独给**，不能从出站结论里推 —— NAT Gateway / LB 的出口地址不是这台 VM 的入站地址，
    /// 混用就会重现"把出口 IP 当入站 IP"的错误（设计决策 §13）。
    /// </summary>
    private void Add(
        string vmResourceId,
        string nicName,
        string vnetName,
        string subnetName,
        string subnetCidr,
        string privateIp,
        VmOutboundOverview outbound,
        string? inboundPublicIp = null)
    {
        var subscriptionId = vmResourceId.Split('/')[2];
        var resourceGroup = vmResourceId.Split('/')[4];
        var nicId = NicId(subscriptionId, resourceGroup, nicName);
        var publicIpId = PublicIpId(subscriptionId, resourceGroup, $"{nicName}-ip");

        _contexts[vmResourceId] = new VmNetworkContext
        {
            VmResourceId = vmResourceId,
            NicName = nicName,
            VnetName = vnetName,
            SubnetName = subnetName,
            SubnetCidr = subnetCidr,
            NsgName = $"nsg-{nicName}",
            NicNsgId = NicNsgId(subscriptionId, resourceGroup, nicName),
            SubnetId = SubnetId(subscriptionId, resourceGroup, vnetName, subnetName),
            PublicIp = inboundPublicIp,
            PublicIpName = inboundPublicIp is null ? null : $"{nicName}-ip",
            PrivateIp = privateIp,
            SecurityState = "Protected",
            // 入站刻意留空：真实订阅里"NSG 只有 Azure 默认规则、没有自定义入站规则"很常见，
            // 这个空表格状态需要有人真的走到
            InboundRules = [],
            OutboundRules = DefaultOutboundRules(subscriptionId, resourceGroup, nicName),
            InboundPublicIps = inboundPublicIp is null
                ? []
                :
                [
                    new InboundPublicIp
                    {
                        Address = inboundPublicIp,
                        Name = $"{nicName}-ip",
                        ResourceId = publicIpId,
                        NicId = nicId,
                        NicName = nicName,
                        IsPrimaryNic = true,
                    }
                ],
            Outbound = outbound,
        };
    }

    /// <summary>
    /// 除 WEB01 外的演示 VM 共用的两条出站规则。
    /// 出站规则是 NSG 里最常见的"一条允许 + 一条拒绝"结构（默认全放行，再按需收口），
    /// 给两条就能同时覆盖 Allow / Deny 两种徽章。
    /// </summary>
    private static List<NsgSecurityRule> DefaultOutboundRules(
        string subscriptionId, string resourceGroup, string nicName)
    {
        var nicNsgId = NicNsgId(subscriptionId, resourceGroup, nicName);

        return
        [
            new()
            {
                RuleId = $"{nicNsgId}/securityRules/AllowHttpsOut", Name = "AllowHttpsOut",
                Source = "Any", SourcePrefix = "*",
                Destination = "Internet", DestinationPrefix = "Internet",
                DestinationPort = 443, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Allow, Origin = NsgRuleOrigin.Nic, Priority = 4000,
                Direction = NsgRuleDirection.Outbound
            },
            new()
            {
                RuleId = $"{nicNsgId}/securityRules/DenyRdpOut", Name = "DenyRdpOut",
                Source = "Any", SourcePrefix = "*",
                Destination = "Internet", DestinationPrefix = "Internet",
                DestinationPort = 3389, Protocol = NsgProtocol.TCP,
                Action = NsgRuleAction.Deny, Origin = NsgRuleOrigin.Nic, Priority = 4010,
                Direction = NsgRuleDirection.Outbound
            }
        ];
    }

    // ==== 出站演示数据 ====

    private static VmOutboundOverview InstancePublicIpOutbound()
    {
        const string nicId = "…/networkInterfaces/web01-nic";
        return SingleNic(
            nicId, "web01-nic", "snet-web",
            Result(
                OutboundConnectivityType.InstancePublicIp, "20.51.13.7",
                "出站使用网卡上的实例级公网 IP；同一个地址也承担入站。微软文档：实例级公网 IP 的优先级仅次于 NAT Gateway。",
                "…/publicIPAddresses/web01-ip"),
            [
                Candidate(
                    OutboundConnectivityType.InstancePublicIp, "20.51.13.7", "…/publicIPAddresses/web01-ip",
                    "网卡 web01-nic 上的实例级公网 IP", isEffective: true)
            ]);
    }

    private static VmOutboundOverview NatGatewayOutbound()
    {
        return SingleNic(
            "…/networkInterfaces/app01-nic", "app01-nic", "snet-app",
            Result(
                OutboundConnectivityType.NatGateway, "20.51.13.20",
                "出站经 NAT Gateway。微软文档明确 NAT Gateway 的优先级高于其他所有出站方式"
                + "（含负载均衡器、实例级公网 IP 与 Azure Firewall）。",
                "…/natGateways/nat-prod"),
            [
                Candidate(
                    OutboundConnectivityType.NatGateway, "20.51.13.20", "…/natGateways/nat-prod",
                    $"子网 snet-app 关联的 NAT Gateway（nat-prod）", isEffective: true),
                // 被压制的候选照样列出来：用户需要知道这台机器上还挂着别的出站方式
                Candidate(
                    OutboundConnectivityType.InstancePublicIp, "20.51.13.21", "…/publicIPAddresses/app01-ip",
                    "网卡 app01-nic 上的实例级公网 IP", isEffective: false)
            ]);
    }

    private static VmOutboundOverview LoadBalancerOutbound()
    {
        return SingleNic(
            "…/networkInterfaces/mon01-nic", "mon01-nic", "snet-ops",
            Result(
                OutboundConnectivityType.LoadBalancerOutboundRule, "20.51.13.30",
                "出站由负载均衡器的出站规则（Outbound Rule）提供，出口地址是规则绑定的前端公网 IP。",
                "…/loadBalancers/lb-mon"),
            [
                Candidate(
                    OutboundConnectivityType.LoadBalancerOutboundRule, "20.51.13.30", "…/loadBalancers/lb-mon",
                    "负载均衡器 lb-mon 的出站规则 outbound-rule-mon", isEffective: true)
            ]);
    }

    private static VmOutboundOverview FirewallRouteOutbound()
    {
        return SingleNic(
            "…/networkInterfaces/bastion01-nic", "bastion01-nic", "snet-bastion",
            Result(
                OutboundConnectivityType.UserDefinedRoute, "20.51.13.40",
                "子网的 0.0.0.0/0 路由把出站流量重定向到网络虚拟设备 / 网关，不由虚拟机网卡直接出网。",
                "…/azureFirewalls/fw-hub"),
            [
                Candidate(
                    OutboundConnectivityType.UserDefinedRoute, "20.51.13.40", "…/azureFirewalls/fw-hub",
                    "子网 snet-bastion 的路由表（rt-bastion）的 0.0.0.0/0 路由 to-firewall → 网络虚拟设备 10.0.0.4",
                    isEffective: true,
                    reason: "经网络虚拟设备 10.0.0.4 出口：出站经 Azure Firewall，出口公网 IP 为 20.51.13.40。")
            ],
            routedToVirtualAppliance: true);
    }

    private static VmOutboundOverview DefaultOutbound()
    {
        // 本轮最关键的一条：出口 IP 确定不可得，但**确实存在**公网出口
        return SingleNic(
            "…/networkInterfaces/dev01-nic", "dev01-nic", "snet-dev",
            Result(
                OutboundConnectivityType.DefaultOutboundAccess, null,
                "未检测到任何显式出站方式，使用 Azure 默认出站访问。 出口地址无法通过 ARM 获得，"
                + "这不代表没有公网出口。",
                evidence: null,
                discoverableByArm: false,
                hasPublicEgress: true),
            [
                Candidate(
                    OutboundConnectivityType.DefaultOutboundAccess, null, null,
                    "Azure 默认出站访问", isEffective: true, discoverable: false,
                    reason: "默认出站使用的公网 IP 由微软持有并分配，不属于你的资源，因此无法通过 ARM Resource API 获得。")
            ]);
    }

    private static VmOutboundOverview NoEgressOutbound()
    {
        return SingleNic(
            "…/networkInterfaces/sql01-nic", "sql01-nic", "snet-data",
            Result(
                OutboundConnectivityType.None, null,
                "未检测到公网出口：没有 NAT Gateway、实例级公网 IP、负载均衡器出站规则或指向出口设备的路由，"
                + "且默认出站访问不可用。",
                evidence: null,
                hasPublicEgress: false),
            [
                Candidate(
                    OutboundConnectivityType.None, null, null, "子网已关闭默认出站访问",
                    isEffective: true,
                    reason: "未检测到任何显式出站方式，且该子网的默认出站访问已关闭，因此不存在公网出口。")
            ]);
    }

    private static VmOutboundOverview UnresolvedRouteOutbound()
    {
        return SingleNic(
            "…/networkInterfaces/oldsql-nic", "oldsql-nic", "snet-legacy",
            Result(
                OutboundConnectivityType.UserDefinedRoute, null,
                "子网的 0.0.0.0/0 路由把出站流量重定向到网络虚拟设备 / 网关，不由虚拟机网卡直接出网。"
                + " 0.0.0.0/0 指向网络虚拟设备 10.0.6.9。CloudFlow 未能把它解析到订阅内的虚拟机或 "
                + "Azure Firewall（可能是跨订阅 / 跨租户设备，或该地址不是网卡私网 IP），因此无法给出最终公网出口 IP。",
                evidence: null,
                discoverableByArm: false),
            [
                Candidate(
                    OutboundConnectivityType.UserDefinedRoute, null, null,
                    "子网 snet-legacy 的路由表（rt-legacy）的 0.0.0.0/0 路由 to-nva",
                    isEffective: true, discoverable: false,
                    reason: "0.0.0.0/0 指向网络虚拟设备 10.0.6.9，未能解析到订阅内的资源。")
            ],
            routedToVirtualAppliance: true);
    }

    private static VmOutboundOverview MixedOutbound()
    {
        var primary = Result(
            OutboundConnectivityType.InstancePublicIp, "20.51.13.50",
            "出站使用网卡上的实例级公网 IP；同一个地址也承担入站。",
            "…/publicIPAddresses/jumpbox-ip");

        var secondary = Result(
            OutboundConnectivityType.None, null,
            "未检测到公网出口：该网卡所在子网已关闭默认出站访问，且没有其他出站方式。",
            evidence: null,
            hasPublicEgress: false);

        return new VmOutboundOverview
        {
            Effective = Result(
                OutboundConnectivityType.Mixed, null,
                "该虚拟机的 2 块网卡出站方式不一致，因此不合并成一个结论："
                + "jumpbox-nic → 实例级公网 IP；jumpbox-nic-mgmt → 无公网出口。请按网卡分别判断。",
                evidence: null,
                discoverableByArm: true,
                hasPublicEgress: true),
            Nics =
            [
                new NicOutboundDetail
                {
                    NicId = "…/networkInterfaces/jumpbox-nic", NicName = "jumpbox-nic", IsPrimary = true,
                    SubnetId = "…/subnets/snet-shared", SubnetName = "snet-shared", Result = primary,
                },
                new NicOutboundDetail
                {
                    NicId = "…/networkInterfaces/jumpbox-nic-mgmt", NicName = "jumpbox-nic-mgmt", IsPrimary = false,
                    SubnetId = "…/subnets/snet-mgmt", SubnetName = "snet-mgmt", Result = secondary,
                }
            ],
            Candidates =
            [
                Candidate(
                    OutboundConnectivityType.InstancePublicIp, "20.51.13.50", "…/publicIPAddresses/jumpbox-ip",
                    "网卡 jumpbox-nic 上的实例级公网 IP", isEffective: true),
                Candidate(
                    OutboundConnectivityType.None, null, null, "子网 snet-mgmt 已关闭默认出站访问",
                    isEffective: true,
                    reason: "该网卡所在子网未检测到任何出站方式。")
            ],
            NicsDiffer = true,
        };
    }

    private static VmOutboundOverview SingleNic(
        string nicId,
        string nicName,
        string subnetName,
        VmOutboundConnectivity result,
        IReadOnlyList<OutboundCandidate> candidates,
        bool routedToVirtualAppliance = false)
        => new()
        {
            Effective = result,
            Nics =
            [
                new NicOutboundDetail
                {
                    NicId = nicId,
                    NicName = nicName,
                    IsPrimary = true,
                    SubnetName = subnetName,
                    RoutedToVirtualAppliance = routedToVirtualAppliance,
                    Result = result,
                }
            ],
            Candidates = candidates,
            NicsDiffer = false,
        };

    private static VmOutboundConnectivity Result(
        OutboundConnectivityType type,
        string? ip,
        string explanation,
        string? evidence,
        bool discoverableByArm = true,
        bool hasPublicEgress = true)
        => new()
        {
            Type = type,
            OutboundIp = ip,
            DiscoverableByArm = discoverableByArm,
            HasPublicEgress = hasPublicEgress,
            EvidenceResourceId = evidence,
            Explanation = explanation,
        };

    private static OutboundCandidate Candidate(
        OutboundConnectivityType type,
        string? ip,
        string? resourceId,
        string source,
        bool isEffective,
        bool discoverable = true,
        string? reason = null)
        => new()
        {
            Type = type,
            Ip = ip,
            ResourceId = resourceId,
            Source = source,
            IsEffective = isEffective,
            DiscoverableByArm = discoverable,
            Reason = reason,
        };

    private const string Sub = "11111111-1111-1111-1111-111111111111";

    private static string VmId(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{name}";

    private static string NicId(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/networkInterfaces/{name}";

    private static string NicNsgId(string subscriptionId, string resourceGroup, string nicName) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/networkSecurityGroups/nsg-{nicName}";

    private static string PublicIpId(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/publicIPAddresses/{name}";

    private static string SubnetId(
        string subscriptionId, string resourceGroup, string vnetName, string subnetName) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network"
        + $"/virtualNetworks/{vnetName}/subnets/{subnetName}";
}
