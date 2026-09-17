using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Demo 数据面的 NSG 规则执行器：改内存里的演示上下文，不触碰 Azure。
/// 真实账户下绝不注册到执行链路上（由 VmNetworkRuleExecutorRouter 按 ProviderType 分流）。
/// </summary>
public sealed class MockVmNetworkRuleExecutor(MockVmNetworkService network) : IVmNetworkRuleExecutor
{
    public async Task<string?> OpenPortAsync(
        OperationRequest request, string nsgId, NsgRuleDraft draft, CancellationToken ct = default)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);

        network.AddRule(request.ResourceId, new NsgSecurityRule
        {
            RuleId = NsgRuleIds.Compose(nsgId, draft.Name),
            Name = draft.Name,
            Source = draft.SourceDisplay,
            SourcePrefix = draft.SourcePrefix,
            // 入站目标的展示文本恒为 null（目标固定是 "*"），此时显示 Any
            Destination = draft.DestinationDisplay ?? "Any",
            DestinationPrefix = draft.DestinationPrefix,
            DestinationPort = draft.Port,
            Protocol = draft.Protocol,
            Action = NsgRuleAction.Allow,
            Origin = OriginOf(request),
            Priority = draft.Priority,
            Direction = draft.Direction
        });

        return Guid.NewGuid().ToString("D");
    }

    public async Task<string?> ChangePortAsync(
        OperationRequest request, string nsgId, string ruleName, int newPort, CancellationToken ct = default)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);

        var ruleId = NsgRuleIds.Compose(nsgId, ruleName);
        if (!network.TryChangePort(request.ResourceId, ruleId, newPort))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到规则：{ruleName}");
        }

        return Guid.NewGuid().ToString("D");
    }

    public async Task<string?> DeleteRuleAsync(
        OperationRequest request, string nsgId, string ruleName, CancellationToken ct = default)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);

        var ruleId = NsgRuleIds.Compose(nsgId, ruleName);
        if (!network.TryDeleteRule(request.ResourceId, ruleId))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到规则：{ruleName}");
        }

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>Demo 只有一份演示上下文，无法按子网统计；返回 null 让影响分析降级。</summary>
    public Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    private static NsgRuleOrigin OriginOf(OperationRequest request) =>
        Enum.TryParse<NsgRuleOrigin>(request.Payload.GetValueOrDefault("origin", "Nic"), out var origin)
            ? origin
            : NsgRuleOrigin.Nic;
}
