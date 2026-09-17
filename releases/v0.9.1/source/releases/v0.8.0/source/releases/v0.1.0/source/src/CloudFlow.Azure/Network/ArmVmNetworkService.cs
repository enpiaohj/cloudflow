using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Network;

/// <summary>
/// ARM 实现的 VM 网络上下文读取（设计文档 §20/§21）：
/// VM → NIC → IP 配置 → 子网 / 公网 IP / NSG，并把 NIC 级与子网级 NSG 的规则按方向聚合展示，
/// 用户不需要跨 Resource Provider 自己找 NSG。
///
/// 只读实现：规则的新增 / 修改 / 删除仍必须经 Operation Engine（§29），本类不写 Azure。
/// 企业与个人账户都通过 <see cref="IAzureClientFactory"/> 取凭据，不假设存在 MSAL 会话。
/// </summary>
public sealed class ArmVmNetworkService : IVmNetworkService
{
    private readonly IAzureClientFactory _clientFactory;
    private readonly ScopeContext _scopeContext;
    private readonly ArmOutboundGraphReader _outboundReader;
    private readonly ILogger<ArmVmNetworkService> _logger;

    public ArmVmNetworkService(
        IAzureClientFactory clientFactory,
        ScopeContext scopeContext,
        ArmOutboundGraphReader outboundReader,
        ILogger<ArmVmNetworkService> logger)
    {
        _clientFactory = clientFactory;
        _scopeContext = scopeContext;
        _outboundReader = outboundReader;
        _logger = logger;
    }

    public async Task<VmNetworkContext?> GetForVmAsync(string vmResourceId, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(vmResourceId, ct).ConfigureAwait(false);
        var vm = await armClient
            .GetVirtualMachineResource(new ResourceIdentifier(vmResourceId))
            .GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var nicReferences = vm.Value.Data.NetworkProfile?.NetworkInterfaces;
        if (nicReferences is not { Count: > 0 })
        {
            _logger.LogInformation("VM {ResourceId} 未关联网卡，网络上下文为空", vmResourceId);
            return null;
        }

        // 出站解析按完整资源图走：全部 NIC、全部 IP 配置，再经子网关联到
        // NAT Gateway / 路由表 / 负载均衡器 / 公网 IP（设计决策 §11）
        var nicIds = nicReferences
            .Select(reference => reference.Id?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        var graph = await _outboundReader
            .ReadAsync(armClient, vmResourceId, nicIds, ActiveAccountContext.SubscriptionIdOf(vmResourceId), ct)
            .ConfigureAwait(false);

        // 入站侧仍是"主网卡"口径：PublicIp / NicName / NsgName 都是单值语义，
        // 改口径会连带打断列表的 IP 列与 ssh / mstsc 的连接目标（设计决策 §13）
        var nicId = graph.Nics.FirstOrDefault(n => n.IsPrimary)?.NicId
            ?? graph.Nics.FirstOrDefault()?.NicId
            ?? nicIds[0];

        var nic = await armClient.GetNetworkInterfaceResource(new ResourceIdentifier(nicId))
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);
        var nicData = nic.Value.Data;

        var ipConfiguration = nicData.IPConfigurations?.FirstOrDefault(c => c.Primary == true)
            ?? nicData.IPConfigurations?.FirstOrDefault();

        var subnetId = ipConfiguration?.Subnet?.Id;
        var subnet = subnetId is null
            ? null
            : (await armClient.GetSubnetResource(subnetId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;

        var nicNsg = nicData.NetworkSecurityGroup;
        var subnetNsg = subnet?.NetworkSecurityGroup;

        // 一次 NSG 读取带回**两个方向**的全部规则，再按 Direction 分流：
        // 出入站是同一种资源（Microsoft.Network/networkSecurityGroups/securityRules），
        // 分两次读会多打一次 ARM，且两次之间规则可能已经变了。
        var rules = new List<NsgSecurityRule>();
        if (nicNsg?.Id is not null)
        {
            rules.AddRange(await ReadRulesAsync(armClient, nicNsg.Id, NsgRuleOrigin.Nic, ct)
                .ConfigureAwait(false));
        }

        if (subnetNsg?.Id is not null)
        {
            rules.AddRange(await ReadRulesAsync(armClient, subnetNsg.Id, NsgRuleOrigin.Subnet, ct)
                .ConfigureAwait(false));
        }

        // 公网 IP 是独立资源：地址、资源名、DNS 名称都在它上面（概念图 2 展示 "ip (资源名)"）
        var (publicIp, publicIpName, publicIpFqdn) = await ResolvePublicIpAsync(
            armClient, ipConfiguration?.PublicIPAddress?.Id, ct).ConfigureAwait(false);

        return new VmNetworkContext
        {
            VmResourceId = vmResourceId,
            NicName = nicData.Name ?? "",
            VnetName = subnetId?.Parent?.Name ?? "",
            SubnetName = subnet?.Name ?? "",
            SubnetCidr = subnet?.AddressPrefix ?? subnet?.AddressPrefixes?.FirstOrDefault() ?? "",
            // NIC / 子网响应里的 NSG 是仅含 Id 的引用，展示名必须取自 Resource ID
            NsgName = nicNsg?.Id?.Name ?? subnetNsg?.Id?.Name ?? "",
            // 写操作的目标 NSG：Open Port 需要知道"往哪个 NSG 加规则"，
            // 过去没有这两个 ID，实现只好凭空编造一个规则 ID。
            NicNsgId = nicNsg?.Id?.ToString(),
            SubnetNsgId = subnetNsg?.Id?.ToString(),
            SubnetId = subnetId?.ToString(),
            // 规则只挂在子网级 NSG 上时，变更会影响同子网的其他 VM（§25）
            IsSharedSubnetNsg = nicNsg?.Id is null && subnetNsg?.Id is not null,
            SecurityState = "Protected",
            PublicIp = publicIp,
            PublicIpName = publicIpName,
            PublicIpFqdn = publicIpFqdn,
            PrivateIp = ipConfiguration?.PrivateIPAddress,
            InboundRules = [.. rules
                .Where(r => r.Direction == NsgRuleDirection.Inbound)
                .OrderBy(r => r.Priority)],
            OutboundRules = [.. rules
                .Where(r => r.Direction == NsgRuleDirection.Outbound)
                .OrderBy(r => r.Priority)],
            InboundPublicIps = MapInboundPublicIps(graph),
            // 解析结果一定带 Explanation —— "出口 IP 未知"必须能一路讲到界面，
            // 不允许上层看到 null 后自行理解成"没有公网出口"
            Outbound = VmOutboundResolver.Resolve(graph)
        };
    }

    public async Task<VmOutboundOverview?> GetOutboundAsync(
        string vmResourceId,
        CancellationToken ct = default)
        => (await GetForVmAsync(vmResourceId, ct).ConfigureAwait(false))?.Outbound;

    public async Task<IReadOnlyList<NsgSecurityRule>> GetInboundRulesAsync(
        string vmResourceId,
        CancellationToken ct = default)
    {
        var context = await GetForVmAsync(vmResourceId, ct).ConfigureAwait(false);
        return context?.InboundRules ?? [];
    }

    public async Task<IReadOnlyList<NsgSecurityRule>> GetOutboundRulesAsync(
        string vmResourceId,
        CancellationToken ct = default)
    {
        var context = await GetForVmAsync(vmResourceId, ct).ConfigureAwait(false);
        return context?.OutboundRules ?? [];
    }

    /// <summary>
    /// 按 RuleId 查规则，**两个方向都查**。
    /// 只查入站会让出站规则的 Change Port / Delete 找不到目标，进而在影响分析里
    /// 拿不到 Origin（子网级规则会被误判成网卡级，§25 的影响面确认随之失效）。
    /// </summary>
    public async Task<NsgSecurityRule?> FindRuleAsync(
        string vmResourceId,
        string ruleId,
        CancellationToken ct = default)
    {
        var context = await GetForVmAsync(vmResourceId, ct).ConfigureAwait(false);
        return context is null
            ? null
            : context.InboundRules.Concat(context.OutboundRules).FirstOrDefault(r =>
                string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按资源所属订阅取凭据：租户跟着订阅走，不能拿"首个已发现订阅"或账户主租户顶替，
    /// 否则客户租户（Guest）下的订阅会取到错的租户 Token。
    /// </summary>
    private async Task<ArmClient> CreateClientAsync(string vmResourceId, CancellationToken ct)
    {
        var account = _scopeContext.ActiveAccount
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");

        return await _clientFactory
            .CreateAsync(
                ActiveAccountContext.Create(account, _scopeContext, ActiveAccountContext.SubscriptionIdOf(vmResourceId)),
                ct)
            .ConfigureAwait(false);
    }

    private static async Task<(string? Address, string? Name, string? Fqdn)> ResolvePublicIpAsync(
        ArmClient armClient,
        ResourceIdentifier? publicIpId,
        CancellationToken ct)
    {
        if (publicIpId is null)
        {
            return (null, null, null);
        }

        var publicIp = await armClient.GetPublicIPAddressResource(publicIpId)
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);
        return (publicIp.Value.Data.IPAddress?.ToString(),
                publicIpId.Name,
                // DnsSettings 在公网 IP 资源本身上，与地址同一次 GET —— 没有额外往返
                publicIp.Value.Data.DnsSettings?.Fqdn);
    }

    /// <summary>
    /// 全部网卡上的**入站**公网 IP。挂在网卡上的公网 IP 同时是入站地址与"实例级出站"的候选，
    /// 但两侧语义独立：这里只负责入站视图，出站走 <see cref="VmOutboundResolver"/>（设计决策 §13）。
    /// </summary>
    private static IReadOnlyList<InboundPublicIp> MapInboundPublicIps(OutboundGraph graph)
    {
        var result = new List<InboundPublicIp>();

        foreach (var nic in graph.Nics)
        {
            foreach (var configuration in nic.IpConfigurations)
            {
                if (configuration.PublicIpId is null
                    || !graph.PublicIps.TryGetValue(configuration.PublicIpId, out var publicIp)
                    || string.IsNullOrWhiteSpace(publicIp.IpAddress))
                {
                    continue;
                }

                result.Add(new InboundPublicIp
                {
                    Address = publicIp.IpAddress,
                    Name = new ResourceIdentifier(publicIp.ResourceId).Name,
                    ResourceId = publicIp.ResourceId,
                    Fqdn = publicIp.Fqdn,
                    NicId = nic.NicId,
                    NicName = nic.NicName,
                    IsPrimaryNic = nic.IsPrimary,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 读一个 NSG 的**全部自定义规则**（两个方向都读），并标上方向与来源（网卡级 / 子网级）。
    /// 不在这里按方向过滤：调用方一次拿到全量再分流，免得为同一个 NSG 打两次 GET。
    /// 注意 <c>SecurityRules</c> 只有自定义规则，Azure 的默认规则在 <c>DefaultSecurityRules</c> 里，
    /// 因此"列表为空"是常态（默认的 AllowInternetOutbound 之类不会出现在这里）。
    /// </summary>
    private static async Task<IReadOnlyList<NsgSecurityRule>> ReadRulesAsync(
        ArmClient armClient,
        ResourceIdentifier nsgId,
        NsgRuleOrigin origin,
        CancellationToken ct)
    {
        var nsg = await armClient.GetNetworkSecurityGroupResource(nsgId)
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);
        var securityRules = nsg.Value.Data.SecurityRules;
        if (securityRules is null)
        {
            return [];
        }

        return [.. securityRules.Select(rule => MapRule(nsgId, rule, origin))];
    }

    private static NsgSecurityRule MapRule(ResourceIdentifier nsgId, SecurityRuleData rule, NsgRuleOrigin origin)
    {
        var sourcePrefix = FirstNonEmpty(rule.SourceAddressPrefix, rule.SourceAddressPrefixes) ?? "*";
        var destinationPrefix =
            FirstNonEmpty(rule.DestinationAddressPrefix, rule.DestinationAddressPrefixes) ?? "*";

        return new NsgSecurityRule
        {
            RuleId = $"{nsgId}/securityRules/{rule.Name}",
            Name = rule.Name ?? "",
            Source = DescribeSource(sourcePrefix),
            SourcePrefix = sourcePrefix,
            Destination = DescribeSource(destinationPrefix),
            DestinationPrefix = destinationPrefix,
            DestinationPort = ParsePort(rule.DestinationPortRange, rule.DestinationPortRanges),
            Protocol = MapProtocol(rule.Protocol),
            Action = rule.Access == SecurityRuleAccess.Allow ? NsgRuleAction.Allow : NsgRuleAction.Deny,
            Origin = origin,
            Priority = rule.Priority ?? 0,
            // SecurityRuleDirection 同样是不能用于 switch 的可扩展结构体，逐个比较静态属性
            Direction = rule.Direction == SecurityRuleDirection.Outbound
                ? NsgRuleDirection.Outbound
                : NsgRuleDirection.Inbound
        };
    }

    /// <summary>
    /// 地址前缀的显示文本：保持 Azure 原语义，不臆造“My IP”。
    /// 入站的来源与出站的目标共用它 —— 两者都是"某一侧的地址前缀"，措辞一致才不会
    /// 让同一串 CIDR 在两张表里显示成两种东西。
    /// </summary>
    private static string DescribeSource(string prefix) => prefix switch
    {
        "*" or "" => "Any",
        "AzureLoadBalancer" => "AzureLoadBalancer",
        "Internet" => "Internet",
        "VirtualNetwork" => "VirtualNetwork",
        _ => prefix
    };

    /// <summary>端口范围（如 "80-443"）取起始端口；"*" 记 0 表示任意端口。</summary>
    private static int ParsePort(string? portRange, IList<string>? portRanges)
    {
        var value = FirstNonEmpty(portRange, portRanges);
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return 0;
        }

        var start = value.Split('-')[0].Trim();
        return int.TryParse(start, out var port) ? port : 0;
    }

    // SecurityRuleProtocol 是可扩展枚举（静态只读字段，非 const），不能用于 switch 模式
    private static NsgProtocol MapProtocol(SecurityRuleProtocol? protocol) =>
        protocol == SecurityRuleProtocol.Tcp ? NsgProtocol.TCP
        : protocol == SecurityRuleProtocol.Udp ? NsgProtocol.UDP
        : NsgProtocol.Any;

    private static string? FirstNonEmpty(string? value, IList<string>? values) =>
        !string.IsNullOrEmpty(value) ? value : values?.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
