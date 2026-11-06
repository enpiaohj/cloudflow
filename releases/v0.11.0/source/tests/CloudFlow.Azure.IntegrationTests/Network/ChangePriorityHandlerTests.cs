using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

public sealed class ChangePriorityHandlerTests
{
    private const string Vm = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/WEB01";
    private sealed class Counter : ISubnetVmCounter
    {
        public Task<int?> CountVmsInSubnetAsync(OperationRequest request, string subnetId, CancellationToken ct = default) => Task.FromResult<int?>(3);
    }
    private static ChangePriorityHandler Handler(MockVmNetworkService network) => new(network,
        new MockVmNetworkRuleExecutor(network), new Counter(), NullLogger<ChangePriorityHandler>.Instance);
    private static OperationRequest Request(NsgSecurityRule rule, string priority, bool preApproved = false) => new()
    {
        PreApproved = preApproved, OperationType = "network.change_priority", AccountId = "demo-account",
        TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", SubscriptionId = "11111111-1111-1111-1111-111111111111",
        ResourceId = Vm, Payload = new Dictionary<string, string> { ["ruleId"] = rule.RuleId, ["priority"] = priority }
    };

    [Fact]
    public async Task 编辑优先级只改当前规则并能验证结果()
    {
        var network = new MockVmNetworkService();
        var rule = (await network.GetInboundRulesAsync(Vm)).First(r => r.Origin == NsgRuleOrigin.Nic);
        var original = (rule.DestinationPort, rule.Protocol, rule.Action, rule.SourcePrefix, rule.DestinationPrefix);
        var request = Request(rule, "360");
        var handler = Handler(network);
        await handler.ValidateAsync(request, default);
        await handler.ExecuteAsync(request, default);
        Assert.True(await handler.VerifyAsync(request, null, default));
        Assert.Equal(original, (rule.DestinationPort, rule.Protocol, rule.Action, rule.SourcePrefix, rule.DestinationPrefix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 网卡优先级变更遵循审批策略(bool preApproved)
    {
        var network = new MockVmNetworkService();
        var rule = (await network.GetInboundRulesAsync(Vm)).First(r => r.Origin == NsgRuleOrigin.Nic);
        var request = Request(rule, "360", preApproved);
        var impact = await Handler(network).AnalyzeImpactAsync(request, default);
        Assert.True(impact.RequiresApproval);
        Assert.False(impact.CannotBypass);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("4097")]
    [InlineData("abc")]
    public async Task 拒绝非法优先级(string priority)
    {
        var network = new MockVmNetworkService();
        var rule = (await network.GetInboundRulesAsync(Vm)).First();
        await Assert.ThrowsAsync<OperationValidationException>(() => Handler(network).ValidateAsync(Request(rule, priority), default));
    }

    [Fact]
    public async Task 同方向同NSG冲突在提交与执行时都拒绝()
    {
        var network = new MockVmNetworkService();
        var rules = (await network.GetInboundRulesAsync(Vm)).Where(r => r.Origin == NsgRuleOrigin.Nic).ToArray();
        var request = Request(rules[0], rules[1].Priority.ToString());
        var handler = Handler(network);
        await Assert.ThrowsAsync<OperationValidationException>(() => handler.ValidateAsync(request, default));
        await Assert.ThrowsAsync<OperationValidationException>(() => handler.ExecuteAsync(request, default));
    }

    [Fact]
    public async Task 保留自身优先级不被误认为冲突()
    {
        var network = new MockVmNetworkService();
        var rule = (await network.GetInboundRulesAsync(Vm)).First();
        await Handler(network).ValidateAsync(Request(rule, rule.Priority.ToString()), default);
    }

    [Fact]
    public async Task 子网级优先级变更显示共享影响面并必须确认()
    {
        var network = new MockVmNetworkService();
        var rule = (await network.GetInboundRulesAsync(Vm)).First(r => r.Origin == NsgRuleOrigin.Subnet);
        var impact = await Handler(network).AnalyzeImpactAsync(Request(rule, "360"), default);
        Assert.True(impact.RequiresApproval);
        Assert.True(impact.CannotBypass);
        Assert.Equal(3, impact.AffectedResources);
        Assert.Contains("匹配顺序", impact.Description);
    }
}
