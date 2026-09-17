using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

/// <summary>
/// 入站 / 出站两条链路的差异点：方向决定用户填的那个对端落在**哪一侧**。
///
/// 这一组断言存在的理由：入站与出站在 NSG 里是同一种资源，写错方向不会报错 ——
/// 只会安静地建出一条"来源 = 我的 IP、目标 = 我的 IP"之类谁也拦不住谁的规则。
/// </summary>
public class OpenPortDirectionTests
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

    private static OperationRequest Request(
        string nsgId,
        NsgRuleDirection direction,
        string peer,
        string ruleName,
        bool preApproved = true,
        string? directionText = null,
        NsgRuleOrigin origin = NsgRuleOrigin.Nic)
    {
        var isOutbound = direction == NsgRuleDirection.Outbound;

        return new OperationRequest
        {
            OperationType = "network.open_port",
            AccountId = "demo-account",
            TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceId = VmResourceId,
            PreApproved = preApproved,
            Payload = new Dictionary<string, string>
            {
                ["nsgId"] = nsgId,
                ["ruleName"] = ruleName,
                ["port"] = "8443",
                ["protocol"] = "TCP",
                ["direction"] = directionText ?? direction.ToString(),
                ["origin"] = origin.ToString(),
                ["priority"] = "400",
                ["sourcePrefix"] = isOutbound ? "*" : peer,
                ["sourceDisplay"] = isOutbound ? "Any" : peer,
                ["destinationPrefix"] = isOutbound ? peer : "*",
                ["destinationDisplay"] = isOutbound ? peer : "Any"
            }
        };
    }

    private static async Task<string> NicNsgIdAsync(MockVmNetworkService network) =>
        (await network.GetForVmAsync(VmResourceId))!.NicNsgId!;

    [Fact]
    public async Task 入站规则_用户填的对端落在来源侧()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);

        await handler.ExecuteAsync(
            Request(nicNsgId, NsgRuleDirection.Inbound, "203.0.113.10/32", "In-8443"),
            CancellationToken.None);

        var rule = (await network.GetInboundRulesAsync(VmResourceId)).Single(r => r.Name == "In-8443");

        Assert.Equal(NsgRuleDirection.Inbound, rule.Direction);
        Assert.Equal("203.0.113.10/32", rule.SourcePrefix);
        // 入站的目标固定是 Any —— 对端只能出现在来源侧
        Assert.Equal("*", rule.DestinationPrefix);
        Assert.Equal("Any", rule.Destination);
    }

    [Fact]
    public async Task 出站规则_用户填的对端落在目标侧()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);

        await handler.ExecuteAsync(
            Request(nicNsgId, NsgRuleDirection.Outbound, "10.0.3.0/24", "Out-8443"),
            CancellationToken.None);

        var rule = (await network.GetOutboundRulesAsync(VmResourceId)).Single(r => r.Name == "Out-8443");

        Assert.Equal(NsgRuleDirection.Outbound, rule.Direction);
        Assert.Equal("10.0.3.0/24", rule.DestinationPrefix);
        // 出站的来源是这台虚拟机自己，不能把对端复制到来源侧
        Assert.Equal("*", rule.SourcePrefix);
        Assert.Equal("Any", rule.Source);
    }

    [Fact]
    public async Task 出站规则不会混进入站列表()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        var inboundBefore = (await network.GetInboundRulesAsync(VmResourceId)).Count;

        await handler.ExecuteAsync(
            Request(nicNsgId, NsgRuleDirection.Outbound, "Internet", "Out-Only"),
            CancellationToken.None);

        Assert.Equal(inboundBefore, (await network.GetInboundRulesAsync(VmResourceId)).Count);
        Assert.Contains(
            await network.GetOutboundRulesAsync(VmResourceId),
            r => r.Name == "Out-Only");
    }

    [Fact]
    public async Task 出站规则的验证查的是出站列表()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        var request = Request(nicNsgId, NsgRuleDirection.Outbound, "Internet", "Out-Verify");

        Assert.False(await handler.VerifyAsync(request, null, CancellationToken.None));

        await handler.ExecuteAsync(request, CancellationToken.None);

        // 拿入站列表去验会永远为 false，把一次成功的写操作判成失败
        Assert.True(await handler.VerifyAsync(request, null, CancellationToken.None));
    }

    [Fact]
    public async Task 缺少direction时按入站处理_既有行为不变()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);

        var payload = new Dictionary<string, string>
        {
            ["nsgId"] = nicNsgId,
            ["ruleName"] = "Legacy-8443",
            ["port"] = "8443",
            ["protocol"] = "TCP",
            ["sourcePrefix"] = "203.0.113.10/32",
            ["sourceDisplay"] = "My IP (203.0.113.10)",
            ["origin"] = nameof(NsgRuleOrigin.Nic),
            ["priority"] = "400"
        };

        var request = new OperationRequest
        {
            OperationType = "network.open_port",
            AccountId = "demo-account",
            TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceId = VmResourceId,
            PreApproved = true,
            Payload = payload
        };

        await handler.ValidateAsync(request, CancellationToken.None);
        await handler.ExecuteAsync(request, CancellationToken.None);

        var rule = (await network.GetInboundRulesAsync(VmResourceId)).Single(r => r.Name == "Legacy-8443");
        Assert.Equal(NsgRuleDirection.Inbound, rule.Direction);
        Assert.Equal("203.0.113.10/32", rule.SourcePrefix);
    }

    [Fact]
    public async Task direction取值非法时校验阶段直接拒绝()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);

        var request = Request(nicNsgId, NsgRuleDirection.Outbound, "*", "Bad-Direction", directionText: "Both");

        // 猜成入站会安静地建到相反的方向上，所以不认识的值必须拒绝
        await Assert.ThrowsAsync<CloudFlow.Core.Errors.OperationValidationException>(() =>
            handler.ValidateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task 出站规则的对端是任意时触发审批()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        // PreApproved = false 才会走"要不要审批"的判定
        var request = Request(nicNsgId, NsgRuleDirection.Outbound, "*", "Out-Any", preApproved: false);

        var impact = await handler.AnalyzeImpactAsync(request, CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        // 文案不能沿用入站那套"将对 Internet 暴露该服务" —— 出站不暴露任何东西给外部
        Assert.DoesNotContain("暴露", impact.Description);
        Assert.Contains("出站", impact.Description);
    }

    [Fact]
    public async Task 出站规则的对端是具体地址时不触发审批()
    {
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        var request = Request(nicNsgId, NsgRuleDirection.Outbound, "10.0.3.0/24", "Out-Narrow", preApproved: false);

        var impact = await handler.AnalyzeImpactAsync(request, CancellationToken.None);

        Assert.False(impact.RequiresApproval);
    }

    // ==== 设计文档 §25：共享子网 NSG 的影响面确认 ====

    [Fact]
    public async Task 子网级NSG的新增规则_影响面确认不可被绕过()
    {
        var (handler, network) = Build();
        var subnetNsgId = (await network.GetForVmAsync(VmResourceId))!.SubnetNsgId!;

        // PreApproved = true 模拟"设置里把审批策略调成关闭"，或者任何调用点忘了传 false。
        // 这两种情况下 §25 都必须照常弹 —— 它是"这一改会波及同子网其它虚拟机"的告知，
        // 不是可以按偏好关掉的打扰。
        var request = Request(subnetNsgId, NsgRuleDirection.Inbound, "203.0.113.10/32", "Subnet-Narrow",
            preApproved: true, origin: NsgRuleOrigin.Subnet);

        var impact = await handler.AnalyzeImpactAsync(request, CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        Assert.True(impact.CannotBypass);
        Assert.Contains("共享网络安全组", impact.Description);
    }

    [Fact]
    public async Task 网卡级NSG的新增规则_不是共享变更_不等于不可绕过()
    {
        // 反向断言：网卡级 NSG 只挂在这一台上，牵连不到别人。
        // 如果这里也置 CannotBypass，用户就再也没法用设置把这类打扰关掉了。
        var (handler, network) = Build();
        var nicNsgId = await NicNsgIdAsync(network);
        var request = Request(nicNsgId, NsgRuleDirection.Inbound, "203.0.113.10/32", "Nic-Narrow",
            preApproved: true, origin: NsgRuleOrigin.Nic);

        var impact = await handler.AnalyzeImpactAsync(request, CancellationToken.None);

        Assert.False(impact.RequiresApproval);
        Assert.False(impact.CannotBypass);
    }
}
