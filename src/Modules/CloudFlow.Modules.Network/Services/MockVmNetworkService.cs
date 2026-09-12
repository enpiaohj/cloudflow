using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Mock VM 网络服务（Demo 模式）。
/// VM-WEB01 的数据与 UI 概念图 2 完全一致：共享 Subnet NSG nsg-web-prod + 6 条 Inbound Rules。
/// 规则变更由 Operation Handler 调用本类的 Mutate 方法完成（数据面），审批面在 Engine。
/// </summary>
public sealed class MockVmNetworkService(MockCurrentIpProvider currentIp) : IVmNetworkService
{
    private readonly Dictionary<string, VmNetworkContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    public MockVmNetworkService() : this(new MockCurrentIpProvider())
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

    public Task<NsgSecurityRule?> FindRuleAsync(string vmResourceId, string ruleId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var rule = _contexts.TryGetValue(vmResourceId, out var ctx)
                ? ctx.InboundRules.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
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

    // ==== 供 Operation Handler 调用的数据面操作 ====

    /// <summary>Change Port（§22）：只改端口，其他字段不动。</summary>
    public bool TryChangePort(string vmResourceId, string ruleId, int newPort)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(vmResourceId, out var ctx))
            {
                return false;
            }
            var rule = ctx.InboundRules.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                return false;
            }
            rule.DestinationPort = newPort;
            return true;
        }
    }

    /// <summary>Open Port（§23）：追加规则。</summary>
    public NsgSecurityRule AddRule(string vmResourceId, NsgSecurityRule rule)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(vmResourceId, out var ctx))
            {
                throw new InvalidOperationException($"No network context for {vmResourceId}");
            }
            var rules = ctx.InboundRules.ToList();
            rules.Add(rule);
            _contexts[vmResourceId] = ctx with { InboundRules = rules };
            return rule;
        }
    }

    /// <summary>Delete Rule（§21）。</summary>
    public bool TryDeleteRule(string vmResourceId, string ruleId)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(vmResourceId, out var ctx))
            {
                return false;
            }
            var rules = ctx.InboundRules.ToList();
            var removed = rules.RemoveAll(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
            _contexts[vmResourceId] = ctx with { InboundRules = rules };
            return removed > 0;
        }
    }

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

        _contexts[web01] = new VmNetworkContext
        {
            VmResourceId = web01,
            NicName = "web01-nic",
            VnetName = "vnet-prod",
            SubnetName = "snet-web",
            SubnetCidr = "10.0.1.0/24",
            NsgName = "nsg-web-prod",
            IsSharedSubnetNsg = true,
            SecurityState = "Protected",
            InboundRules = rules
        };
    }
}
