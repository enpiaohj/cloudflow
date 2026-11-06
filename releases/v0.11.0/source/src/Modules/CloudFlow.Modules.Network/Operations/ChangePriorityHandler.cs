using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Network.Operations;

public sealed class ChangePriorityHandler(IVmNetworkService network, IVmNetworkRuleExecutor executor,
    ISubnetVmCounter subnetCounter, ILogger<ChangePriorityHandler> logger)
    : NetworkRuleHandlerBase("network.change_priority", network, executor, subnetCounter, logger)
{
    public override async Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        await base.ValidateAsync(request, ct).ConfigureAwait(false);
        if (!int.TryParse(request.Payload.GetValueOrDefault("priority"), out var priority) || priority is < 100 or > 4096)
            throw new OperationValidationException("优先级必须在 100–4096 之间。");
        var rule = await RequireRuleAsync(request, ct).ConfigureAwait(false);
        var (nsgId, _) = RequireTarget(request);
        var rules = rule.Direction == NsgRuleDirection.Outbound
            ? await Network.GetOutboundRulesAsync(request.ResourceId, ct).ConfigureAwait(false)
            : await Network.GetInboundRulesAsync(request.ResourceId, ct).ConfigureAwait(false);
        var conflict = rules.FirstOrDefault(r => r.Priority == priority &&
            !string.Equals(r.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NsgRuleIds.Split(r.RuleId)?.NsgId, nsgId, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
            throw new OperationValidationException($"优先级 {priority} 已被同方向规则「{conflict.Name}」使用，请选择其他优先级。");
    }

    protected override async Task<ImpactAssessment> AssessImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var rule = await RequireRuleAsync(request, ct).ConfigureAwait(false);
        var context = await GetContextAsync(request, ct).ConfigureAwait(false);
        var description = $"将规则「{rule.Name}」的优先级从 {rule.Priority} 改为 {request.Payload["priority"]}，可能改变允许/拒绝规则的匹配顺序";
        var shared = await SharedNsgImpactAsync(request, context, rule.Origin, description, ct).ConfigureAwait(false);
        return shared.RequiresApproval ? shared : new ImpactAssessment
        {
            RequiresApproval = true, AffectedResources = 1,
            Description = description + "。请确认不会影响当前连接。"
        };
    }

    public override Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var (nsgId, ruleName) = RequireTarget(request);
        return Executor.ChangePriorityAsync(request, nsgId, ruleName, int.Parse(request.Payload["priority"]), ct);
    }

    public override async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rule = await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct).ConfigureAwait(false);
        return rule?.Priority == int.Parse(request.Payload["priority"]);
    }

    private async Task<NsgSecurityRule> RequireRuleAsync(OperationRequest request, CancellationToken ct) =>
        await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct).ConfigureAwait(false)
        ?? throw new OperationValidationException("规则已不存在，请刷新列表后重试。");
}
