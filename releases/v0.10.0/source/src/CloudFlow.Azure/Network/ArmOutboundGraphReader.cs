using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Modules.Network.Models;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Network;

/// <summary>
/// 按资源图读取 VM 出站连通性所需的全部输入（设计文档 §20/§21）：
/// VM → NIC → IP Configuration → Subnet → NAT Gateway / Route Table / Load Balancer / Public IP。
///
/// 过去这里只取 <c>NetworkProfile.NetworkInterfaces[0]</c> 与主 IP 配置，第二块网卡直接丢失；
/// 本类遍历**全部 NIC 与全部 IP 配置**，并按资源 ID 去重（多网卡共用子网是常态）。
///
/// 读取失败的跳不会抛出，而是记进 <see cref="OutboundGraph.ReadFailures"/> ——
/// "读不到"必须能传到界面上，不能被当成"没有"。
/// </summary>
public sealed class ArmOutboundGraphReader(
    ArmOutboundGraphQueries queries,
    ILogger<ArmOutboundGraphReader> logger)
{
    /// <summary>NVA 递归深度上限。再深就是长链推理，每跳还要一次 ARG 查询。</summary>
    private const int MaxNvaDepth = 1;

    public Task<OutboundGraph> ReadAsync(
        ArmClient armClient,
        string vmResourceId,
        IReadOnlyList<string> nicIds,
        string? subscriptionId,
        CancellationToken ct = default)
        => ReadAsync(armClient, vmResourceId, nicIds, subscriptionId, 0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vmResourceId }, ct);

    private async Task<OutboundGraph> ReadAsync(
        ArmClient armClient,
        string vmResourceId,
        IReadOnlyList<string> nicIds,
        string? subscriptionId,
        int depth,
        HashSet<string> visitedVmIds,
        CancellationToken ct)
    {
        var failures = new List<string>();
        var nics = new List<OutboundNic>();
        var subnets = new Dictionary<string, OutboundSubnet>(StringComparer.OrdinalIgnoreCase);
        var natGateways = new Dictionary<string, OutboundNatGateway>(StringComparer.OrdinalIgnoreCase);
        var loadBalancers = new Dictionary<string, OutboundLoadBalancer>(StringComparer.OrdinalIgnoreCase);
        var routeTables = new Dictionary<string, OutboundRouteTable>(StringComparer.OrdinalIgnoreCase);
        var publicIps = new Dictionary<string, OutboundPublicIp>(StringComparer.OrdinalIgnoreCase);
        var publicIpPrefixes = new Dictionary<string, OutboundPublicIpPrefix>(StringComparer.OrdinalIgnoreCase);

        var subnetIds = new List<string>();
        var publicIpIds = new List<string>();
        var prefixIds = new List<string>();
        var natGatewayIds = new List<string>();
        var routeTableIds = new List<string>();
        var loadBalancerIds = new List<string>();

        // 第 1 层：全部网卡
        foreach (var nicId in nicIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = (await armClient.GetNetworkInterfaceResource(new ResourceIdentifier(nicId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                var configurations = new List<OutboundIpConfig>();
                foreach (var ipConfig in data.IPConfigurations ?? [])
                {
                    var poolIds = (ipConfig.LoadBalancerBackendAddressPools ?? [])
                        .Select(pool => pool.Id?.ToString())
                        .Where(IsId)
                        .Select(id => id!)
                        .ToList();

                    configurations.Add(new OutboundIpConfig
                    {
                        IsPrimary = ipConfig.Primary == true,
                        SubnetId = ipConfig.Subnet?.Id?.ToString(),
                        PublicIpId = ipConfig.PublicIPAddress?.Id?.ToString(),
                        LoadBalancerBackendPoolIds = poolIds,
                    });
                }

                nics.Add(new OutboundNic
                {
                    NicId = data.Id?.ToString() ?? nicId,
                    NicName = data.Name ?? "",
                    IsPrimary = data.Primary == true,
                    DefaultOutboundConnectivityEnabled = data.DefaultOutboundConnectivityEnabled,
                    IpConfigurations = configurations,
                });

                foreach (var configuration in configurations)
                {
                    AddIfId(subnetIds, configuration.SubnetId);
                    AddIfId(publicIpIds, configuration.PublicIpId);
                    foreach (var poolId in configuration.LoadBalancerBackendPoolIds)
                    {
                        AddIfId(loadBalancerIds, LoadBalancerIdOf(poolId));
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"读取网卡 {nicId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取网卡 {NicId} 失败", nicId);
            }
        }

        // 第 3 层：子网（去重后每种子网只读一次）
        foreach (var subnetId in subnetIds)
        {
            try
            {
                var data = (await armClient.GetSubnetResource(new ResourceIdentifier(subnetId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                subnets[subnetId] = new OutboundSubnet
                {
                    SubnetId = data.Id?.ToString() ?? subnetId,
                    Name = data.Name ?? "",
                    NatGatewayId = data.NatGatewayId,
                    RouteTableId = data.RouteTable?.Id?.ToString(),
                    DefaultOutboundAccess = data.DefaultOutboundAccess,
                };

                AddIfId(natGatewayIds, data.NatGatewayId);
                AddIfId(routeTableIds, data.RouteTable?.Id?.ToString());
            }
            catch (Exception ex)
            {
                failures.Add($"读取子网 {subnetId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取子网 {SubnetId} 失败", subnetId);
            }
        }

        // 第 4 层：NAT Gateway
        foreach (var natGatewayId in natGatewayIds)
        {
            try
            {
                var data = (await armClient.GetNatGatewayResource(new ResourceIdentifier(natGatewayId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                var ipIds = SubResourceIds(data.PublicIPAddresses, data.PublicIPAddressesV6);
                var prefixResourceIds = SubResourceIds(data.PublicIPPrefixes, data.PublicIPPrefixesV6);

                natGateways[natGatewayId] = new OutboundNatGateway
                {
                    ResourceId = data.Id?.ToString() ?? natGatewayId,
                    Name = data.Name ?? "",
                    PublicIpIds = ipIds,
                    PublicIpPrefixIds = prefixResourceIds,
                };

                publicIpIds.AddRange(ipIds);
                prefixIds.AddRange(prefixResourceIds);
            }
            catch (Exception ex)
            {
                failures.Add($"读取 NAT Gateway {natGatewayId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取 NAT Gateway {NatGatewayId} 失败", natGatewayId);
            }
        }

        // 第 4 层：负载均衡器
        foreach (var loadBalancerId in loadBalancerIds)
        {
            try
            {
                var loadBalancer = await ReadLoadBalancerAsync(armClient, loadBalancerId, ct)
                    .ConfigureAwait(false);
                loadBalancers[loadBalancerId] = loadBalancer;
                publicIpIds.AddRange(loadBalancer.FrontendPublicIpIds.Values);
            }
            catch (Exception ex)
            {
                failures.Add($"读取负载均衡器 {loadBalancerId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取负载均衡器 {LoadBalancerId} 失败", loadBalancerId);
            }
        }

        // 第 4 层：路由表
        foreach (var routeTableId in routeTableIds)
        {
            try
            {
                var data = (await armClient.GetRouteTableResource(new ResourceIdentifier(routeTableId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                routeTables[routeTableId] = new OutboundRouteTable
                {
                    ResourceId = data.Id?.ToString() ?? routeTableId,
                    Name = data.Name ?? "",
                    Routes =
                    [
                        .. (data.Routes ?? []).Select(route => new OutboundRoute
                        {
                            Name = route.Name ?? "",
                            AddressPrefix = route.AddressPrefix ?? "",
                            // 可扩展枚举转字符串后自己比较，不用 SDK 枚举做 switch（本仓已吃过这个亏）
                            NextHopType = route.NextHopType?.ToString() ?? "",
                            NextHopIpAddress = route.NextHopIPAddress,
                        })
                    ],
                };
            }
            catch (Exception ex)
            {
                failures.Add($"读取路由表 {routeTableId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取路由表 {RouteTableId} 失败", routeTableId);
            }
        }

        // 第 4 层：公网 IP / 公网 IP 前缀
        foreach (var publicIpId in publicIpIds.Where(IsId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = (await armClient.GetPublicIPAddressResource(new ResourceIdentifier(publicIpId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                publicIps[publicIpId] = new OutboundPublicIp
                {
                    ResourceId = data.Id?.ToString() ?? publicIpId,
                    IpAddress = data.IPAddress?.ToString(),
                    SkuName = data.Sku?.Name?.ToString(),
                    PrefixId = data.PublicIPPrefixId?.ToString(),
                    // DNS 名称标签是可选的：没配就是 null，与"读取失败"无关，不记入 failures
                    DnsLabel = data.DnsSettings?.DomainNameLabel,
                    Fqdn = data.DnsSettings?.Fqdn,
                };

                AddIfId(prefixIds, data.PublicIPPrefixId?.ToString());
            }
            catch (Exception ex)
            {
                failures.Add($"读取公网 IP {publicIpId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取公网 IP {PublicIpId} 失败", publicIpId);
            }
        }

        foreach (var prefixId in prefixIds.Where(IsId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = (await armClient.GetPublicIPPrefixResource(new ResourceIdentifier(prefixId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                publicIpPrefixes[prefixId] = new OutboundPublicIpPrefix
                {
                    ResourceId = data.Id?.ToString() ?? prefixId,
                    IpPrefix = data.IPPrefix,
                    PrefixLength = data.PrefixLength,
                };
            }
            catch (Exception ex)
            {
                failures.Add($"读取公网 IP 前缀 {prefixId} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "读取公网 IP 前缀 {PrefixId} 失败", prefixId);
            }
        }

        // NVA 递归（限深 + 防环）
        var nvaResults = await ResolveNvasAsync(
            armClient, routeTables.Values, vmResourceId, subscriptionId, depth, visitedVmIds, failures, ct)
            .ConfigureAwait(false);

        return new OutboundGraph
        {
            VmResourceId = vmResourceId,
            Nics = nics,
            Subnets = subnets,
            NatGateways = natGateways,
            LoadBalancers = loadBalancers,
            RouteTables = routeTables,
            PublicIps = publicIps,
            PublicIpPrefixes = publicIpPrefixes,
            ReadFailures = failures,
            NvaResults = nvaResults,
        };
    }

    /// <summary>
    /// UDR 的下一跳只写了一串私网地址，必须反查才能知道那台设备是谁：
    /// 先查 Azure Firewall，再查网卡（从而找到它属于哪台 VM）。两者都是直连出口时就不再往深走。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, VmOutboundConnectivity>> ResolveNvasAsync(
        ArmClient armClient,
        IEnumerable<OutboundRouteTable> routeTables,
        string vmResourceId,
        string? subscriptionId,
        int depth,
        HashSet<string> visitedVmIds,
        List<string> failures,
        CancellationToken ct)
    {
        var results = new Dictionary<string, VmOutboundConnectivity>(StringComparer.OrdinalIgnoreCase);

        var addresses = routeTables
            .SelectMany(table => table.Routes)
            .Where(route => route.NextHopType.Equals("VirtualAppliance", StringComparison.OrdinalIgnoreCase))
            .Select(route => route.NextHopIpAddress)
            .Where(IsId)
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0 || depth >= MaxNvaDepth)
        {
            return results;
        }

        foreach (var address in addresses)
        {
            try
            {
                var firewall = await ResolveFirewallAsync(armClient, address, subscriptionId, ct)
                    .ConfigureAwait(false);

                if (firewall is not null)
                {
                    results[address] = firewall;
                    continue;
                }

                var fromNic = await ResolveNvaVmAsync(
                    armClient, address, vmResourceId, subscriptionId, depth, visitedVmIds, failures, ct)
                    .ConfigureAwait(false);

                if (fromNic is not null)
                {
                    results[address] = fromNic;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"解析下一跳 {address} 失败：{Describe(ex)}");
                logger.LogWarning(ex, "解析 UDR 下一跳 {Address} 失败", address);
            }
        }

        return results;
    }

    private async Task<VmOutboundConnectivity?> ResolveFirewallAsync(
        ArmClient armClient,
        string address,
        string? subscriptionId,
        CancellationToken ct)
    {
        var matches = await queries.FindFirewallsByPrivateIpAsync(address, subscriptionId, ct).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            // 订阅内多个 Firewall 用同一个私网 IP：无法确定是哪一台，不能随便挑一个。
            return Unresolved($"私网地址 {address} 在订阅内匹配到 {matches.Count} 个 Azure Firewall，无法确定是哪一台。");
        }

        var match = matches[0];
        var publicIpIds = new List<string>();
        AddIfId(publicIpIds, match.PublicIpId);

        var addresses = await ReadPublicIpAddressesAsync(armClient, publicIpIds, ct).ConfigureAwait(false);
        var ip = addresses.FirstOrDefault();

        return new VmOutboundConnectivity
        {
            Type = OutboundConnectivityType.UserDefinedRoute,
            OutboundIp = ip,
            DiscoverableByArm = ip is not null,
            HasPublicEgress = true,
            EvidenceResourceId = match.FirewallId,
            Explanation = ip is null
                ? $"出站经 Azure Firewall（{match.FirewallId}），但其公网 IP 未读取到地址，最终出口 IP 未知。"
                : $"出站经 Azure Firewall（{match.FirewallId}），出口公网 IP 为 {ip}。",
        };
    }

    private async Task<VmOutboundConnectivity?> ResolveNvaVmAsync(
        ArmClient armClient,
        string address,
        string vmResourceId,
        string? subscriptionId,
        int depth,
        HashSet<string> visitedVmIds,
        List<string> failures,
        CancellationToken ct)
    {
        var matches = await queries.FindNicsByPrivateIpAsync(address, subscriptionId, ct).ConfigureAwait(false);
        var candidates = matches.Where(m => IsId(m.VmId) && !string.IsNullOrWhiteSpace(m.VmId)).ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count > 1)
        {
            return Unresolved($"私网地址 {address} 在订阅内匹配到 {candidates.Count} 块网卡，无法确定是哪一台网络虚拟设备。");
        }

        var nvaVmId = candidates[0].VmId;
        if (visitedVmIds.Contains(nvaVmId))
        {
            // 防环：NVA 的路由又指回自己（或指回被分析的 VM）时停下，不转圈。
            return Unresolved($"下一跳 {address} 指向虚拟机 {nvaVmId}，该虚拟机已在本轮解析路径上（存在路由环路），不再递归。");
        }

        if (depth + 1 > MaxNvaDepth)
        {
            return Unresolved($"下一跳 {address} 指向另一台网络虚拟设备，超出递归深度上限。");
        }

        visitedVmIds.Add(nvaVmId);

        var nicIds = await ReadNicIdsAsync(armClient, nvaVmId, ct).ConfigureAwait(false);
        if (nicIds.Count == 0)
        {
            return Unresolved($"下一跳指向虚拟机 {nvaVmId}，但未能读取它的网卡。");
        }

        var graph = await ReadAsync(armClient, nvaVmId, nicIds, subscriptionId, depth + 1, visitedVmIds, ct)
            .ConfigureAwait(false);
        failures.AddRange(graph.ReadFailures.Select(f => $"解析网络虚拟设备 {nvaVmId} 时：{f}"));

        var overview = VmOutboundResolver.Resolve(graph);
        return overview.Effective with
        {
            Explanation = $"下一跳 {address} 是虚拟机 {nvaVmId}（网络虚拟设备）：{overview.Effective.Explanation}",
        };
    }

    private static VmOutboundConnectivity Unresolved(string explanation) => new()
    {
        Type = OutboundConnectivityType.UserDefinedRoute,
        DiscoverableByArm = false,
        HasPublicEgress = true,
        Explanation = explanation,
    };

    private async Task<IReadOnlyList<string>> ReadNicIdsAsync(
        ArmClient armClient, string vmResourceId, CancellationToken ct)
    {
        var vm = await armClient.GetVirtualMachineResource(new ResourceIdentifier(vmResourceId))
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);

        return
        [
            .. (vm.Value.Data.NetworkProfile?.NetworkInterfaces ?? [])
                .Select(reference => reference.Id?.ToString())
                .Where(IsId)
                .Select(id => id!)
        ];
    }

    private async Task<IReadOnlyList<string>> ReadPublicIpAddressesAsync(
        ArmClient armClient, IReadOnlyList<string> publicIpIds, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var publicIpId in publicIpIds.Where(IsId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = (await armClient.GetPublicIPAddressResource(new ResourceIdentifier(publicIpId))
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

                if (!string.IsNullOrWhiteSpace(data.IPAddress?.ToString()))
                {
                    result.Add(data.IPAddress!.ToString());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "读取公网 IP {PublicIpId} 失败", publicIpId);
            }
        }

        return result;
    }

    private static async Task<OutboundLoadBalancer> ReadLoadBalancerAsync(
        ArmClient armClient, string loadBalancerId, CancellationToken ct)
    {
        var data = (await armClient.GetLoadBalancerResource(new ResourceIdentifier(loadBalancerId))
            .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

        var frontendPublicIpIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frontend in data.FrontendIPConfigurations ?? [])
        {
            var publicIpId = frontend.PublicIPAddress?.Id?.ToString();
            if (!IsId(publicIpId))
            {
                continue;
            }

            // 出站规则引用的是 frontend 的 ID，读 frontend 列表拿到的是对象 —— 两个键都放，不猜。
            if (frontend.Id is not null)
            {
                frontendPublicIpIds[frontend.Id.ToString()] = publicIpId!;
            }

            if (!string.IsNullOrWhiteSpace(frontend.Name))
            {
                frontendPublicIpIds[frontend.Name] = publicIpId!;
            }
        }

        var skuName = data.Sku?.Name?.ToString();

        return new OutboundLoadBalancer
        {
            ResourceId = data.Id?.ToString() ?? loadBalancerId,
            Name = data.Name ?? "",
            IsStandardSku = string.IsNullOrWhiteSpace(skuName)
                ? null
                : skuName.Equals("Standard", StringComparison.OrdinalIgnoreCase),
            BackendAddressPoolIds =
            [
                .. (data.BackendAddressPools ?? [])
                    .Select(pool => pool.Id?.ToString())
                    .Where(IsId)
                    .Select(id => id!)
            ],
            OutboundRules =
            [
                .. (data.OutboundRules ?? []).Select(rule => new OutboundLbOutboundRule
                {
                    Name = rule.Name ?? "",
                    BackendAddressPoolId = rule.BackendAddressPoolId?.ToString(),
                    FrontendIpConfigRefs =
                    [
                        .. (rule.FrontendIPConfigurations ?? [])
                            .Select(frontend => frontend.Id?.ToString())
                            .Where(IsId)
                            .Select(id => id!)
                    ],
                })
            ],
            FrontendPublicIpIds = frontendPublicIpIds,
        };
    }

    /// <summary>后端池 ID 形如 …/loadBalancers/{name}/backendAddressPools/{pool}，从中取出所属 LB 的 ID。</summary>
    private static string? LoadBalancerIdOf(string backendPoolId)
    {
        const string marker = "/backendAddressPools/";
        var index = backendPoolId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : backendPoolId[..index];
    }

    private static List<string> SubResourceIds(
        IEnumerable<WritableSubResource>? first, IEnumerable<WritableSubResource>? second)
    {
        var result = new List<string>();
        foreach (var resource in (first ?? []).Concat(second ?? []))
        {
            AddIfId(result, resource.Id?.ToString());
        }

        return result;
    }

    private static void AddIfId(ICollection<string> target, string? value)
    {
        if (IsId(value))
        {
            target.Add(value!);
        }
    }

    private static bool IsId(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>错误描述只取一段，避免把整页 ARM 报错塞进详情页卡片。</summary>
    private static string Describe(Exception ex)
    {
        var message = ex switch
        {
            RequestFailedException failed => $"{failed.Status} {failed.Message}",
            _ => ex.Message,
        };

        return message.Length <= 200 ? message : message[..200] + "…";
    }
}
