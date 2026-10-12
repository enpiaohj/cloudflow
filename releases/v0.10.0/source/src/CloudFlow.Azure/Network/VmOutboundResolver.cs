using System.Text;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Azure.Network;

/// <summary>
/// VM 出站连通性解析器：吃 <see cref="OutboundGraph"/>，吐 <see cref="VmOutboundOverview"/>。
///
/// **纯函数、无 I/O** —— 这个类不碰 Azure，全部输入都来自已经读好的资源图。这样做的原因只有一个：
/// 优先级判定、多网卡聚合、"读不到 ≠ 没有"这三件最容易出错的事，必须能用 record 直接构造用例测，
/// 而不是靠连到真订阅上碰运气。
///
/// 判定顺序（依据见 <see cref="OutboundConnectivityType"/> 的文档注释）：
/// <code>
/// 先看路由：子网 0.0.0.0/0 若指向 NVA / 网关 → 流量被重定向，不直接出网卡
///   黑洞（NextHopType=None）   → None（确定无出口，优先级最高：包被丢弃，后面的 SNAT 方法都轮不上）
///   VirtualAppliance / 网关   → UserDefinedRoute
///   Internet / 无路由         → 落到下面的 SNAT 方法表
/// 再按微软文档的优先级取第一个命中：
///   NAT Gateway > 实例级公网 IP > LB 出站规则 > LB 隐式 > 默认出站
/// 都没命中才允许报 DefaultOutboundAccess 或 None。
/// </code>
/// </summary>
public static class VmOutboundResolver
{
    private const string DefaultRouteV4 = "0.0.0.0/0";

    private const string DefaultRouteV6 = "::/0";

    public static VmOutboundOverview Resolve(OutboundGraph graph)
    {
        var details = new List<NicOutboundDetail>();
        var candidates = new List<OutboundCandidate>();
        var typeSet = new List<OutboundConnectivityType>();

        foreach (var nic in graph.Nics)
        {
            var resolution = ResolveNic(graph, nic);
            details.Add(new NicOutboundDetail
            {
                NicId = nic.NicId,
                NicName = nic.NicName,
                IsPrimary = nic.IsPrimary,
                SubnetId = resolution.SubnetId,
                SubnetName = resolution.SubnetName,
                RoutedToVirtualAppliance = resolution.RoutedToVirtualAppliance,
                Result = resolution.Result,
            });
            candidates.AddRange(resolution.Candidates);
            typeSet.Add(resolution.Result.Type);
        }

        if (details.Count == 0)
        {
            // 一块网卡都没读到。可能 VM 真没有网卡，也可能是读取阶段就断了 ——
            // 两种都无法支撑"没有公网出口"这个结论。
            return new VmOutboundOverview
            {
                Effective = new VmOutboundConnectivity
                {
                    Type = OutboundConnectivityType.Unknown,
                    DiscoverableByArm = false,
                    HasPublicEgress = true,
                    Explanation = "未能读取该虚拟机的任何网卡，无法判定出站方式。这不等于没有公网出口。",
                },
                Nics = [],
                Candidates = [],
                NicsDiffer = false,
            };
        }

        var distinctTypes = typeSet.Distinct().ToList();
        var effectiveType = distinctTypes.Count == 1 ? distinctTypes[0] : OutboundConnectivityType.Mixed;

        var addresses = details
            .SelectMany(d => AddressesOf(d.Result))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 只有一个裸 IP 时用 OutboundIp 表达；否则（多网卡 / 前缀 / 多个地址）落到列表里，
        // 不截断成"第一个"—— 截断会让用户以为出口只有一个地址。
        var singleIp = addresses.Count == 1 && !addresses[0].Contains('/') ? addresses[0] : null;
        IReadOnlyList<string> cidrs = singleIp is null ? addresses : [];

        var effective = new VmOutboundConnectivity
        {
            Type = effectiveType,
            OutboundIp = singleIp,
            OutboundCidrs = cidrs,
            DiscoverableByArm = details.All(d => d.Result.DiscoverableByArm),
            HasPublicEgress = details.Any(d => d.Result.HasPublicEgress),
            EvidenceResourceId = details.Select(d => d.Result.EvidenceResourceId).FirstOrDefault(id => id is not null),
            Explanation = ComposeAggregateExplanation(graph, details, distinctTypes, effectiveType),
        };

        return new VmOutboundOverview
        {
            Effective = effective,
            Nics = details,
            Candidates = candidates,
            NicsDiffer = distinctTypes.Count > 1,
        };
    }

    private sealed record NicResolution(
        VmOutboundConnectivity Result,
        IReadOnlyList<OutboundCandidate> Candidates,
        string? SubnetId,
        string? SubnetName,
        bool RoutedToVirtualAppliance);

    private static NicResolution ResolveNic(OutboundGraph graph, OutboundNic nic)
    {
        var candidates = new List<OutboundCandidate>();
        var notes = new List<string>();
        var routedToNva = false;

        var subnets = ResolveSubnets(graph, nic, notes);

        CollectNatGatewayCandidates(graph, subnets, candidates);
        CollectInstancePublicIpCandidates(graph, nic, candidates);
        CollectLoadBalancerCandidates(graph, nic, candidates, notes);
        var blackholed = CollectRouteCandidates(graph, subnets, candidates, notes, ref routedToNva);

        if (candidates.Count == 0)
        {
            candidates.Add(ResolveImplicitEgress(graph, nic, subnets, blackholed, notes));
        }

        // 被读取失败挡住的"否定结论"：只有全部相关跳都读成功，才允许说"没有"。
        //
        // 判据是**结论的性质**，不是优先级序号。None 与 DefaultOutboundAccess 都是
        // "没找到别的东西"推出来的否定结论，任何一跳读失败都可能藏着一个被漏掉的显式方法。
        // 用优先级序号判会漏掉 None —— 它在优先级表里排在**最前**（黑洞优先于 SNAT），
        // 于是"私有子网 + 某跳读取失败"会直接报 None，让用户以为出口已经收紧，
        // 而这正是本规则唯一要挡的误判。
        //
        // 读到实据的候选不算否定结论，包括读取失败但确实存在的那几种
        //（如"子网关联了路由表，但读不到该路由表"）：它们本身已经表达了"这里有配置"，
        // 降级成 Unknown 只会丢信息，不会更安全。
        if (graph.ReadFailures.Count > 0 && candidates.All(c => IsNegativeConclusion(c.Type)))
        {
            candidates.Clear();
            candidates.Add(new OutboundCandidate
            {
                Type = OutboundConnectivityType.Unknown,
                IsEffective = true,
                DiscoverableByArm = false,
                Failed = true,
                Reason = string.Join("；", graph.ReadFailures),
            });
        }

        var ranked = candidates.Where(c => c.Type != OutboundConnectivityType.Unknown).ToList();
        if (ranked.Count == 0)
        {
            var unknown = new VmOutboundConnectivity
            {
                Type = OutboundConnectivityType.Unknown,
                DiscoverableByArm = false,
                HasPublicEgress = true,
                Explanation = "无法判定出站方式："
                    + string.Join("；", candidates.Select(c => c.Reason).Where(r => !string.IsNullOrWhiteSpace(r)))
                    + "。这不等于没有公网出口。",
            };
            return new NicResolution(unknown, candidates, subnets.FirstOrDefault()?.SubnetId,
                subnets.FirstOrDefault()?.Name, routedToNva);
        }

        var bestRank = ranked.Min(c => Rank(c.Type));
        for (var i = 0; i < candidates.Count; i++)
        {
            candidates[i] = candidates[i] with { IsEffective = Rank(candidates[i].Type) == bestRank };
        }

        var effective = candidates.Where(c => c.IsEffective).ToList();
        var type = effective[0].Type;
        var effectiveAddresses = effective
            .SelectMany(c => AddressesOf(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var effectiveSingleIp = effectiveAddresses.Count == 1 && !effectiveAddresses[0].Contains('/')
            ? effectiveAddresses[0]
            : null;

        var result = new VmOutboundConnectivity
        {
            Type = type,
            OutboundIp = effectiveSingleIp,
            OutboundCidrs = effectiveSingleIp is null ? effectiveAddresses : (IReadOnlyList<string>)[],
            DiscoverableByArm = type != OutboundConnectivityType.Unknown
                && effective.All(c => c.DiscoverableByArm),
            HasPublicEgress = type != OutboundConnectivityType.None,
            EvidenceResourceId = effective.Select(c => c.ResourceId).FirstOrDefault(id => id is not null),
            Explanation = ComposeNicExplanation(type, effective, candidates, notes),
        };

        return new NicResolution(result, candidates, subnets.FirstOrDefault()?.SubnetId,
            subnets.FirstOrDefault()?.Name, routedToNva);
    }

    /// <summary>
    /// 该网卡涉及的全部子网。多 IP 配置理论上可落在不同子网，因此逐个子网收集，
    /// 但按 Resource ID 去重 —— 多网卡共用子网是常态，不去重会重复判定同一批资源。
    /// </summary>
    private static List<OutboundSubnet> ResolveSubnets(OutboundGraph graph, OutboundNic nic, List<string> notes)
    {
        var result = new List<OutboundSubnet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in nic.IpConfigurations)
        {
            if (string.IsNullOrWhiteSpace(config.SubnetId) || !seen.Add(config.SubnetId))
            {
                continue;
            }

            var subnet = Lookup(graph.Subnets, config.SubnetId);
            if (subnet is null)
            {
                notes.Add($"未能读取子网 {config.SubnetId}");
                continue;
            }

            result.Add(subnet);
        }

        return result;
    }

    private static void CollectNatGatewayCandidates(
        OutboundGraph graph,
        IReadOnlyList<OutboundSubnet> subnets,
        List<OutboundCandidate> candidates)
    {
        foreach (var subnet in subnets)
        {
            if (string.IsNullOrWhiteSpace(subnet.NatGatewayId))
            {
                continue;
            }

            var source = $"子网 {subnet.Name} 关联的 NAT Gateway";
            var gateway = Lookup(graph.NatGateways, subnet.NatGatewayId);

            if (gateway is null)
            {
                candidates.Add(new OutboundCandidate
                {
                    Type = OutboundConnectivityType.NatGateway,
                    ResourceId = subnet.NatGatewayId,
                    Source = source,
                    Failed = true,
                    DiscoverableByArm = false,
                    Reason = "已关联 NAT Gateway，但未能读取该 NAT Gateway 资源。",
                });
                continue;
            }

            var ips = ResolvePublicIps(graph, gateway.PublicIpIds);
            var cidrs = ResolvePublicIpPrefixes(graph, gateway.PublicIpPrefixIds);
            var hasAddress = ips.Count > 0 || cidrs.Count > 0;

            candidates.Add(new OutboundCandidate
            {
                Type = OutboundConnectivityType.NatGateway,
                Ip = ips.FirstOrDefault(),
                Cidrs = [.. ips, .. cidrs],
                ResourceId = gateway.ResourceId,
                Source = $"{source}（{gateway.Name}）",
                Failed = !hasAddress,
                DiscoverableByArm = hasAddress,
                Reason = hasAddress
                    ? null
                    : gateway.PublicIpIds.Count + gateway.PublicIpPrefixIds.Count == 0
                        ? "该 NAT Gateway 尚未关联任何公网 IP 或公网 IP 前缀，当前不提供出站。"
                        : "该 NAT Gateway 的公网地址读取失败，实际出口 IP 未知。",
            });
        }
    }

    private static void CollectInstancePublicIpCandidates(
        OutboundGraph graph,
        OutboundNic nic,
        List<OutboundCandidate> candidates)
    {
        foreach (var config in nic.IpConfigurations)
        {
            if (string.IsNullOrWhiteSpace(config.PublicIpId))
            {
                continue;
            }

            var source = $"网卡 {nic.NicName} 上的实例级公网 IP";
            var publicIp = Lookup(graph.PublicIps, config.PublicIpId);

            if (publicIp is null)
            {
                candidates.Add(new OutboundCandidate
                {
                    Type = OutboundConnectivityType.InstancePublicIp,
                    ResourceId = config.PublicIpId,
                    Source = source,
                    Failed = true,
                    DiscoverableByArm = false,
                    Reason = "网卡上挂着公网 IP 资源，但未能读取其地址。",
                });
                continue;
            }

            var addresses = AddressesOf(publicIp);
            candidates.Add(new OutboundCandidate
            {
                Type = OutboundConnectivityType.InstancePublicIp,
                Ip = addresses.FirstOrDefault(),
                Cidrs = addresses,
                ResourceId = publicIp.ResourceId,
                Source = source,
                Failed = addresses.Count == 0,
                DiscoverableByArm = addresses.Count > 0,
                Reason = addresses.Count == 0
                    ? "该公网 IP 资源尚未分配地址（可能是静态公网 IP 未创建或读取失败）。"
                    : null,
            });
        }
    }

    private static void CollectLoadBalancerCandidates(
        OutboundGraph graph,
        OutboundNic nic,
        List<OutboundCandidate> candidates,
        List<string> notes)
    {
        var poolIds = nic.IpConfigurations
            .SelectMany(c => c.LoadBalancerBackendPoolIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (poolIds.Count == 0)
        {
            return;
        }

        foreach (var loadBalancer in graph.LoadBalancers.Values)
        {
            // 只认本网卡真的在池里的负载均衡器。进池 ≠ 有出站规则，还要看 LB 侧。
            var matchedPool = poolIds.FirstOrDefault(poolId =>
                loadBalancer.BackendAddressPoolIds.Any(p => IdEquals(p, poolId)));

            if (matchedPool is null)
            {
                continue;
            }

            if (loadBalancer.IsStandardSku == false)
            {
                candidates.Add(new OutboundCandidate
                {
                    Type = OutboundConnectivityType.LoadBalancerOutboundRule,
                    ResourceId = loadBalancer.ResourceId,
                    Source = $"负载均衡器 {loadBalancer.Name}",
                    IsEffective = false,
                    DiscoverableByArm = true,
                    Reason = "该负载均衡器是 Basic SKU。Basic SKU 负载均衡器的出站连接已被微软退役，"
                        + "不再承载出站流量。",
                });
                continue;
            }

            var publicFrontends = loadBalancer.FrontendPublicIpIds.Values
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (publicFrontends.Count == 0)
            {
                // 进了池但没有公网前端 → 这个 LB 提供不了公网出站（可能只做内网）。
                notes.Add($"负载均衡器 {loadBalancer.Name} 没有公网前端 IP，不提供公网出站");
                continue;
            }

            var rules = loadBalancer.OutboundRules
                .Where(r => string.IsNullOrWhiteSpace(r.BackendAddressPoolId)
                    || IdEquals(r.BackendAddressPoolId, matchedPool))
                .ToList();

            if (rules.Count == 0)
            {
                if (loadBalancer.OutboundRules.Count > 0)
                {
                    // 该 LB 配了出站规则，但没有一条覆盖本网卡所在的池 —— 本网卡拿不到这个 LB 的出站。
                    notes.Add($"负载均衡器 {loadBalancer.Name} 配有出站规则，但均未覆盖本网卡所在的后端池");
                    continue;
                }

                var implicitAddresses = ResolvePublicIps(graph, publicFrontends);
                candidates.Add(new OutboundCandidate
                {
                    Type = OutboundConnectivityType.LoadBalancerImplicit,
                    Ip = implicitAddresses.FirstOrDefault(),
                    Cidrs = implicitAddresses,
                    ResourceId = loadBalancer.ResourceId,
                    Source = $"负载均衡器 {loadBalancer.Name} 的公网前端（未配置出站规则）",
                    DiscoverableByArm = implicitAddresses.Count > 0,
                    Reason = implicitAddresses.Count == 0
                        ? "未能读取该负载均衡器前端公网 IP 的地址。"
                        : "该负载均衡器未配置出站规则。按微软文档，虚拟机在后端池中且前端有公网 IP 时，"
                            + "前端会隐式承载出站。",
                });
                continue;
            }

            var ruleAddresses = rules
                .SelectMany(r => r.FrontendIpConfigRefs)
                .Select(reference => Lookup(loadBalancer.FrontendPublicIpIds, reference))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var addresses = ResolvePublicIps(graph, ruleAddresses);
            candidates.Add(new OutboundCandidate
            {
                Type = OutboundConnectivityType.LoadBalancerOutboundRule,
                Ip = addresses.FirstOrDefault(),
                Cidrs = addresses,
                ResourceId = loadBalancer.ResourceId,
                Source = $"负载均衡器 {loadBalancer.Name} 的出站规则 {string.Join(" / ", rules.Select(r => r.Name))}",
                DiscoverableByArm = addresses.Count > 0,
                Reason = addresses.Count == 0 ? "已有出站规则，但未能读取其前端公网 IP 的地址。" : null,
            });
        }
    }

    /// <summary>
    /// 路由判定。返回该子网的 0.0.0.0/0 是否被黑洞。
    /// </summary>
    private static bool CollectRouteCandidates(
        OutboundGraph graph,
        IReadOnlyList<OutboundSubnet> subnets,
        List<OutboundCandidate> candidates,
        List<string> notes,
        ref bool routedToNva)
    {
        var blackholed = false;

        foreach (var subnet in subnets)
        {
            if (string.IsNullOrWhiteSpace(subnet.RouteTableId))
            {
                continue;
            }

            var source = $"子网 {subnet.Name} 的路由表";
            var routeTable = Lookup(graph.RouteTables, subnet.RouteTableId);

            if (routeTable is null)
            {
                candidates.Add(new OutboundCandidate
                {
                    Type = OutboundConnectivityType.UserDefinedRoute,
                    ResourceId = subnet.RouteTableId,
                    Source = source,
                    Failed = true,
                    DiscoverableByArm = false,
                    Reason = "子网关联了路由表，但未能读取该路由表，无法确认出站是否被重定向。",
                });
                continue;
            }

            var defaultRoutes = routeTable.Routes.Where(r => IsDefaultRoute(r.AddressPrefix)).ToList();
            if (defaultRoutes.Count == 0)
            {
                // 只有明细路由：不影响公网出站的默认行为。
                continue;
            }

            foreach (var route in defaultRoutes)
            {
                var nextHop = (route.NextHopType ?? string.Empty).Trim();
                var routeSource = $"{source}（{routeTable.Name}）的 0.0.0.0/0 路由 {route.Name}";

                if (nextHop.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    blackholed = true;
                    candidates.Add(new OutboundCandidate
                    {
                        Type = OutboundConnectivityType.None,
                        ResourceId = routeTable.ResourceId,
                        Source = routeSource,
                        DiscoverableByArm = true,
                        Reason = "该路由的下一跳类型为 None（黑洞），发往 Internet 的流量被丢弃。",
                    });
                    continue;
                }

                if (nextHop.Equals("VirtualAppliance", StringComparison.OrdinalIgnoreCase))
                {
                    routedToNva = true;
                    candidates.Add(BuildNvaCandidate(graph, route, routeSource));
                    continue;
                }

                if (nextHop.Equals("VirtualNetworkGateway", StringComparison.OrdinalIgnoreCase))
                {
                    routedToNva = true;
                    candidates.Add(new OutboundCandidate
                    {
                        Type = OutboundConnectivityType.UserDefinedRoute,
                        ResourceId = routeTable.ResourceId,
                        Source = routeSource,
                        DiscoverableByArm = false,
                        Reason = "出站经 VPN / ExpressRoute 虚拟网关转发。对端出口地址不在 Azure 资源模型里，"
                            + "CloudFlow 无法解析到最终公网 IP。",
                    });
                    continue;
                }

                if (nextHop.Equals("Internet", StringComparison.OrdinalIgnoreCase))
                {
                    // 显式指回 Internet：不构成独立出口类型，落到 SNAT 方法表继续判定。
                    continue;
                }

                notes.Add($"{routeSource} 的下一跳类型为 {nextHop}，未参与出站判定");
            }
        }

        return blackholed;
    }

    private static OutboundCandidate BuildNvaCandidate(
        OutboundGraph graph,
        OutboundRoute route,
        string routeSource)
    {
        var address = route.NextHopIpAddress;

        if (string.IsNullOrWhiteSpace(address))
        {
            return new OutboundCandidate
            {
                Type = OutboundConnectivityType.UserDefinedRoute,
                Source = routeSource,
                DiscoverableByArm = false,
                Failed = true,
                Reason = "0.0.0.0/0 指向网络虚拟设备，但该路由未填写下一跳 IP 地址，无法定位设备。",
            };
        }

        if (!graph.NvaResults.TryGetValue(address, out var nva))
        {
            return new OutboundCandidate
            {
                Type = OutboundConnectivityType.UserDefinedRoute,
                Source = routeSource,
                DiscoverableByArm = false,
                Reason = $"0.0.0.0/0 指向网络虚拟设备 {address}。CloudFlow 未能把它解析到订阅内的"
                    + "虚拟机或 Azure Firewall（可能是跨订阅 / 跨租户设备，或该地址不是网卡私网 IP），"
                    + "因此无法给出最终公网出口 IP。",
            };
        }

        return new OutboundCandidate
        {
            Type = OutboundConnectivityType.UserDefinedRoute,
            Ip = nva.OutboundIp,
            Cidrs = nva.OutboundCidrs,
            ResourceId = nva.EvidenceResourceId ?? routeSource,
            Source = $"{routeSource} → 网络虚拟设备 {address}",
            DiscoverableByArm = nva.DiscoverableByArm,
            Reason = $"经网络虚拟设备 {address} 出口：{nva.Explanation}",
        };
    }

    /// <summary>
    /// 没有任何显式方法时，才轮到默认出站 / 无出口的判定 —— 这是"不允许误判"的核心分支。
    /// </summary>
    private static OutboundCandidate ResolveImplicitEgress(
        OutboundGraph graph,
        OutboundNic nic,
        IReadOnlyList<OutboundSubnet> subnets,
        bool blackholed,
        List<string> notes)
    {
        if (blackholed)
        {
            return new OutboundCandidate
            {
                Type = OutboundConnectivityType.None,
                DiscoverableByArm = true,
                Source = "0.0.0.0/0 路由黑洞",
                Reason = "发往 Internet 的流量被路由丢弃，不存在公网出口。",
            };
        }

        // 子网侧：属性缺省（null）按 Azure 语义视为允许默认出站；任一子网允许即视为允许。
        bool? subnetAllows = subnets.Count == 0
            ? null
            : subnets.Any(s => s.DefaultOutboundAccess != false);

        // NIC 侧标志反映的是"Azure 当前是否已给这块网卡分配了默认出站 IP"。
        // 它比子网策略更贴近物理现状，但它只在 stop/deallocate 之后才刷新（微软文档），
        // 因此两者不一致时以 NIC 侧为准（更保守：宁可说"有出口"），并把矛盾讲清楚。
        var nicFlag = nic.DefaultOutboundConnectivityEnabled;
        var allows = nicFlag ?? subnetAllows;

        if (allows == true)
        {
            if (nicFlag == true && subnetAllows == false)
            {
                notes.Add("网卡级 defaultOutboundConnectivityEnabled 为 true，而子网 defaultOutboundAccess 为 false；"
                    + "该标志只在虚拟机停止并重新分配后刷新，两者需要重启后才能一致");
            }

            return new OutboundCandidate
            {
                Type = OutboundConnectivityType.DefaultOutboundAccess,
                DiscoverableByArm = false,
                Source = "Azure 默认出站访问",
                Reason = "默认出站使用的公网 IP 由微软持有并分配，不属于你的资源，"
                    + "因此无法通过 ARM Resource API 获得。",
            };
        }

        if (allows == false)
        {
            return new OutboundCandidate
            {
                Type = OutboundConnectivityType.None,
                DiscoverableByArm = true,
                Source = "子网已关闭默认出站访问",
                Reason = "未检测到任何显式出站方式，且该子网的默认出站访问已关闭，因此不存在公网出口。",
            };
        }

        // 两个信号都没读到 —— 不能猜。
        notes.Add("未能读取子网的默认出站访问设置，也未读到网卡级默认出站标志");
        return new OutboundCandidate
        {
            Type = OutboundConnectivityType.Unknown,
            DiscoverableByArm = false,
            Failed = true,
            Reason = "无法确定该子网是否启用了默认出站访问。",
        };
    }

    private static string ComposeNicExplanation(
        OutboundConnectivityType type,
        IReadOnlyList<OutboundCandidate> effective,
        IReadOnlyList<OutboundCandidate> all,
        IReadOnlyList<string> notes)
    {
        var text = new StringBuilder();
        text.Append(TypeExplanation(type));

        if (type == OutboundConnectivityType.DefaultOutboundAccess)
        {
            text.Append(" 出口地址无法通过 ARM 获得，这不代表没有公网出口。");
        }

        var suppressed = all
            .Where(c => !c.IsEffective && c.Type != OutboundConnectivityType.Unknown)
            .ToList();

        if (suppressed.Count > 0)
        {
            text.Append(" 同时检测到：")
                .Append(string.Join("；", suppressed.Select(c => $"{TypeLabel(c.Type)}（{c.Source ?? "无来源描述"}）")))
                .Append("。");

            var suppressedExplicit = suppressed.Count(c => IsExplicit(c.Type));
            if (suppressedExplicit > 0 && IsExplicit(type))
            {
                text.Append(" 存在多种显式出站方式时，微软只给出优先级，未完整描述叠加后的物理行为，建议人工复核。");
            }
        }

        // 生效项自身的失败说明也要冒出来（例如 NAT Gateway 读了但地址没读到）。
        var effectiveReasons = effective
            .Select(c => c.Reason)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToList();

        foreach (var reason in effectiveReasons)
        {
            text.Append(' ').Append(reason);
        }

        foreach (var note in notes)
        {
            text.Append(" 注意：").Append(note).Append('。');
        }

        return text.ToString();
    }

    private static string ComposeAggregateExplanation(
        OutboundGraph graph,
        IReadOnlyList<NicOutboundDetail> details,
        IReadOnlyList<OutboundConnectivityType> distinctTypes,
        OutboundConnectivityType effectiveType)
    {
        var text = new StringBuilder();

        if (distinctTypes.Count > 1)
        {
            text.Append($"该虚拟机的 {details.Count} 块网卡出站方式不一致，因此不合并成一个结论：");
            text.Append(string.Join("；", details.Select(d => $"{d.NicName} → {TypeLabel(d.Result.Type)}")));
            text.Append("。请按网卡分别判断。");
        }
        else if (details.Count > 1)
        {
            text.Append($"{details.Count} 块网卡的出站方式一致（{TypeLabel(effectiveType)}）。");
            text.Append(details[0].Result.Explanation);
        }
        else
        {
            text.Append(details[0].Result.Explanation);
        }

        if (graph.ReadFailures.Count > 0)
        {
            text.Append(" 读取失败项：").Append(string.Join("；", graph.ReadFailures)).Append('。');
            text.Append(" 这些跳没有读成功，相关结论可能不完整。");
        }

        return text.ToString();
    }

    private static bool IsExplicit(OutboundConnectivityType type) => type
        is OutboundConnectivityType.NatGateway
        or OutboundConnectivityType.UserDefinedRoute
        or OutboundConnectivityType.InstancePublicIp
        or OutboundConnectivityType.LoadBalancerOutboundRule
        or OutboundConnectivityType.LoadBalancerImplicit;

    /// <summary>
    /// 数值越小优先级越高。黑洞（<see cref="OutboundConnectivityType.None"/> 由路由产生时）排在最前：
    /// 包在路由阶段就被丢弃，后面的 SNAT 方法根本轮不上。
    /// 注意 None 只在"没有命中任何显式方法"或"路由黑洞"时才会被放进候选，
    /// 因此它与其他类型同场竞争只可能是黑洞场景，不存在把"私有子网无出口"误排到第一的问题。
    /// </summary>
    private static int Rank(OutboundConnectivityType type) => type switch
    {
        OutboundConnectivityType.None => 0,
        OutboundConnectivityType.NatGateway => 1,
        OutboundConnectivityType.UserDefinedRoute => 2,
        OutboundConnectivityType.InstancePublicIp => 3,
        OutboundConnectivityType.LoadBalancerOutboundRule => 4,
        OutboundConnectivityType.LoadBalancerImplicit => 5,
        OutboundConnectivityType.DefaultOutboundAccess => 6,
        _ => 7,
    };

    /// <summary>
    /// 该结论是否建立在"没找到别的东西"之上。只有这类结论才允许被读取失败推翻 ——
    /// 它们是"没有"的断言，而"没有"必须由完整的读取来支撑。
    /// </summary>
    private static bool IsNegativeConclusion(OutboundConnectivityType type)
        => type is OutboundConnectivityType.None or OutboundConnectivityType.DefaultOutboundAccess;

    private static string TypeExplanation(OutboundConnectivityType type) => type switch
    {
        OutboundConnectivityType.NatGateway =>
            "出站经 NAT Gateway。微软文档明确 NAT Gateway 的优先级高于其他所有出站方式"
            + "（含负载均衡器、实例级公网 IP 与 Azure Firewall）。",
        OutboundConnectivityType.UserDefinedRoute =>
            "子网的 0.0.0.0/0 路由把出站流量重定向到网络虚拟设备 / 网关，不由虚拟机网卡直接出网。",
        OutboundConnectivityType.InstancePublicIp =>
            "出站使用网卡上的实例级公网 IP；同一个地址也承担入站。"
            + "微软文档：实例级公网 IP 的优先级仅次于 NAT Gateway。",
        OutboundConnectivityType.LoadBalancerOutboundRule =>
            "出站由负载均衡器的出站规则（Outbound Rule）提供，出口地址是规则绑定的前端公网 IP。",
        OutboundConnectivityType.LoadBalancerImplicit =>
            "该虚拟机所在的负载均衡器后端池没有出站规则，但该负载均衡器有公网前端；"
            + "按微软文档，此时前端会隐式承载出站。",
        OutboundConnectivityType.DefaultOutboundAccess =>
            "未检测到任何显式出站方式，使用 Azure 默认出站访问。",
        OutboundConnectivityType.None =>
            "未检测到公网出口：没有 NAT Gateway、实例级公网 IP、负载均衡器出站规则或指向出口设备的路由，"
            + "且默认出站访问不可用。",
        OutboundConnectivityType.Mixed => "多块网卡的出站方式不一致。",
        _ => "无法判定出站方式。",
    };

    // 标签与"未知 / 无出口"的文案统一放在 Modules.Network 的 OutboundText 里：
    // 界面与解析器共用一份措辞，「查不到 ≠ 没有」这条规则也能被测试断言。
    private static string TypeLabel(OutboundConnectivityType type) => OutboundText.TypeLabel(type);

    private static IReadOnlyList<string> AddressesOf(VmOutboundConnectivity connectivity)
    {
        if (!string.IsNullOrWhiteSpace(connectivity.OutboundIp))
        {
            return [connectivity.OutboundIp, .. connectivity.OutboundCidrs];
        }

        return connectivity.OutboundCidrs;
    }

    private static IReadOnlyList<string> AddressesOf(OutboundCandidate candidate)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.Ip))
        {
            list.Add(candidate.Ip);
        }

        list.AddRange(candidate.Cidrs.Where(c => !string.IsNullOrWhiteSpace(c)));
        return list;
    }

    private static IReadOnlyList<string> AddressesOf(OutboundPublicIp publicIp)
    {
        if (string.IsNullOrWhiteSpace(publicIp.IpAddress))
        {
            return [];
        }

        return [publicIp.IpAddress];
    }

    private static List<string> ResolvePublicIps(OutboundGraph graph, IReadOnlyList<string> ids)
    {
        var result = new List<string>();
        foreach (var id in ids)
        {
            var publicIp = Lookup(graph.PublicIps, id);
            if (publicIp is not null && !string.IsNullOrWhiteSpace(publicIp.IpAddress))
            {
                result.Add(publicIp.IpAddress);
            }
        }

        return result;
    }

    private static List<string> ResolvePublicIpPrefixes(OutboundGraph graph, IReadOnlyList<string> ids)
    {
        var result = new List<string>();
        foreach (var id in ids)
        {
            var prefix = Lookup(graph.PublicIpPrefixes, id);
            if (prefix is not null && !string.IsNullOrWhiteSpace(prefix.IpPrefix))
            {
                result.Add(prefix.IpPrefix);
            }
        }

        return result;
    }

    private static bool IsDefaultRoute(string? prefix)
    {
        var value = prefix?.Trim();
        return string.Equals(value, DefaultRouteV4, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, DefaultRouteV6, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Azure Resource ID 大小写不敏感，但字典的键来自 ARM 响应，大小写不保证与引用处一致。
    /// 这里先按原样查，再退化成不敏感扫描 —— 资源数量是个位数，不值得为此要求每个读取方都构造敏感字典。
    /// </summary>
    private static T? Lookup<T>(IReadOnlyDictionary<string, T> map, string? id)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (map.TryGetValue(id, out var direct))
        {
            return direct;
        }

        foreach (var pair in map)
        {
            if (IdEquals(pair.Key, id))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
