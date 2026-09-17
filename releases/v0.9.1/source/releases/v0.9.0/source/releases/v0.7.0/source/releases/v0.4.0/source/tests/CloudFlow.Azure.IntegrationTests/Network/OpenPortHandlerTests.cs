using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

/// <summary>
/// Open Port 的完整链路（Demo 数据面）：
/// Handler 不再依赖具体的 Mock 服务，而是通过 IVmNetworkRuleExecutor 写目标 NSG。
/// </summary>
public class OpenPortHandlerTests
{
    private const string VmResourceId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/WEB01";

    private sealed class NullSubnetCounter : ISubnetVmCounter
    {
        public Task<int?> CountVmsInSubnetAsync(
            OperationRequest request, string subnetId, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
    }

    private static (OpenPortHandler Handler, MockVmNetworkService Network) Build()
    {
        var network = new MockVmNetworkService();
        var executor = new MockVmNetworkRuleExecutor(network);
        var handler = new OpenPortHandler(
            network, executor, new NullSubnetCounter(), NullLogger<OpenPortHandler>.Instance);
        return (handler, network);
    }

    private static OperationRequest Request(string nsgId, params (string Key, string Value)[] extra)
    {
        var payload = new Dictionary<string, string>
        {
            ["nsgId"] = nsgId,
            ["ruleName"] = "CloudFlow-8443",
            ["port"] = "8443",
            ["protocol"] = "TCP",
            ["sourcePrefix"] = "203.0.113.10/32",
            ["sourceDisplay"] = "My IP (203.0.113.10)",
            ["origin"] = nameof(NsgRuleOrigin.Nic),
            ["priority"] = "400"
        };
        foreach (var (key, value) in extra)
        {
            payload[key] = value;
        }

        return new OperationRequest
        {
            OperationType = "network.open_port",
            AccountId = "demo-account",
            TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceId = VmResourceId,
            PreApproved = true,
            Payload = payload
        };
    }

    private static async Task<string> NicNsgIdAsync(MockVmNetworkService network)
    {
        var context = await network.GetForVmAsync(VmResourceId);
        return context!.NicNsgId!;
    }

    [Fact]
    public async Task 执行后_规则落在指名的那一个NSG上()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);

        await handler.ExecuteAsync(Request(nicNsgId), CancellationToken.None);

        var rule = (await network.GetInboundRulesAsync(VmResourceId))
            .Single(r => r.Name == "CloudFlow-8443");

        // 过去这里会得到 "{vmId}/rules/CloudFlow-8443-xxxxxxxx"，与读路径的规则 ID 形状不一致，
        // 导致真实账户下永远找不到目标 NSG。
        Assert.Equal(NsgRuleIds.Compose(nicNsgId, "CloudFlow-8443"), rule.RuleId);
        Assert.Equal(8443, rule.DestinationPort);
        Assert.Equal(NsgRuleAction.Allow, rule.Action);
    }

    [Fact]
    public async Task 指定子网NSG时_规则落在子网NSG上()
    {
        var (handler, network) = Build();
        var context = await network.GetForVmAsync(VmResourceId);

        await handler.ExecuteAsync(
            Request(context!.SubnetNsgId!, ("origin", nameof(NsgRuleOrigin.Subnet))),
            CancellationToken.None);

        var rules = await network.GetInboundRulesAsync(VmResourceId);
        var rule = rules.Single(r => r.Name == "CloudFlow-8443");
        Assert.StartsWith(context.SubnetNsgId!, rule.RuleId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NsgRuleOrigin.Subnet, rule.Origin);
    }

    [Fact]
    public async Task 缺少nsgId时_校验阶段直接拒绝()
    {
        var (handler, _) = Build();

        var payload = new Dictionary<string, string>
        {
            ["ruleName"] = "X",
            ["port"] = "8443"
        };

        await Assert.ThrowsAsync<CloudFlow.Core.Errors.OperationValidationException>(() =>
            handler.ValidateAsync(
                new OperationRequest
                {
                    OperationType = "network.open_port",
                    AccountId = "a",
                    TenantId = "t",
                    SubscriptionId = "s",
                    ResourceId = VmResourceId,
                    Payload = payload
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task 验证阶段_规则确实存在才算成功()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        var request = Request(nicNsgId);

        Assert.False(await handler.VerifyAsync(request, null, CancellationToken.None));

        await handler.ExecuteAsync(request, CancellationToken.None);

        Assert.True(await handler.VerifyAsync(request, null, CancellationToken.None));
    }
}
