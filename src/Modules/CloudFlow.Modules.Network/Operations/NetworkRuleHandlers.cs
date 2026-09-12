using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Network.Operations;

/// <summary>
/// Port Manager 操作处理器：Change Port / Open Port / Delete Rule。
/// 执行过程遵循设计文档 §22：读取 Rule → Validate → Impact Analysis → Update → Verify → Audit。
/// </summary>
public abstract class NetworkRuleHandlerBase(
    string operationType,
    MockVmNetworkService network,
    ILogger logger) : IOperationHandler
{
    protected const int MinPort = 1;
    protected const int MaxPort = 65535;

    public string OperationType => operationType;

    protected MockVmNetworkService Network { get; } = network;

    protected abstract ImpactAssessment AssessImpact(OperationRequest request);

    public virtual Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        if (!request.Payload.TryGetValue("ruleId", out var ruleId) || string.IsNullOrEmpty(ruleId))
        {
            throw new OperationValidationException("ruleId is required.");
        }

        if (request.Payload.TryGetValue("port", out var portText))
        {
            if (!int.TryParse(portText, out var port) || port is < MinPort or > MaxPort)
            {
                throw new OperationValidationException($"Port must be between {MinPort} and {MaxPort}.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var impact = AssessImpact(request);

        // Subnet NSG 上 source=Any 的新规则会暴露到 Internet，必须审批（§25 原则）
        if (impact.RequiresApproval && !request.PreApproved)
        {
            logger.LogWarning("Operation {Operation} requires approval: {Reason}",
                OperationType, impact.Description);
        }

        return Task.FromResult(impact);
    }

    public abstract Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct);

    public abstract Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct);

    /// <summary>共享 Subnet NSG 上的变更：按设计文档 §25 要求确认影响面。</summary>
    protected static ImpactAssessment SharedNsgImpact(VmNetworkContext? ctx, string description)
    {
        if (ctx is { IsSharedSubnetNsg: true })
        {
            return new ImpactAssessment
            {
                RequiresApproval = true,
                Description = $"Shared subnet NSG '{ctx.NsgName}': {description}. Potentially affects all VMs in subnet '{ctx.SubnetName}'.",
                AffectedResources = 1
            };
        }
        return ImpactAssessment.None;
    }
}

/// <summary>network.change_port：只改端口，其他字段保持不变（§22）。</summary>
public sealed class ChangePortHandler(
    MockVmNetworkService network,
    ILogger<ChangePortHandler> logger) : NetworkRuleHandlerBase("network.change_port", network, logger)
{
    protected override ImpactAssessment AssessImpact(OperationRequest request)
    {
        var ctx = Network.GetForVmAsync(request.ResourceId).GetAwaiter().GetResult();
        return SharedNsgImpact(ctx, $"change port of rule '{request.Payload.GetValueOrDefault("ruleName", request.Payload["ruleId"])}'");
    }

    public async override Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var ruleId = request.Payload["ruleId"];
        var newPort = int.Parse(request.Payload["port"]);

        await Task.Delay(400, ct).ConfigureAwait(false);

        if (!Network.TryChangePort(request.ResourceId, ruleId, newPort))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"Rule '{ruleId}' not found.");
        }

        logger.LogInformation("Change port of rule {Rule} to {Port} on {Resource}", ruleId, newPort, request.ResourceId);
        return Guid.NewGuid().ToString("D");
    }

    public async override Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rule = await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct).ConfigureAwait(false);
        return rule is not null && rule.DestinationPort == int.Parse(request.Payload["port"]);
    }
}

/// <summary>network.open_port：新建入站规则（§23）。</summary>
public sealed class OpenPortHandler(
    MockVmNetworkService network,
    ILogger<OpenPortHandler> logger) : NetworkRuleHandlerBase("network.open_port", network, logger)
{
    protected override ImpactAssessment AssessImpact(OperationRequest request)
    {
        var ctx = Network.GetForVmAsync(request.ResourceId).GetAwaiter().GetResult();
        var source = request.Payload.GetValueOrDefault("sourcePrefix", "*");
        var isInternet = source is "*" or "0.0.0.0/0" or "Internet";
        var description = isInternet
            ? $"open port {request.Payload.GetValueOrDefault("port")} to Internet"
            : $"open port {request.Payload.GetValueOrDefault("port")}";

        var impact = SharedNsgImpact(ctx, description);
        if (impact.RequiresApproval)
        {
            return impact;
        }

        // NIC NSG 上对 Internet 开放也提示审批（安全默认）
        return isInternet
            ? new ImpactAssessment
            {
                RequiresApproval = true,
                Description = $"{description} exposes the port to the Internet. Approval required.",
                AffectedResources = 1
            }
            : ImpactAssessment.None;
    }

    public async override Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var port = int.Parse(request.Payload["port"]);
        var name = request.Payload.GetValueOrDefault("ruleName", $"port-{port}");
        var sourcePrefix = request.Payload.GetValueOrDefault("sourcePrefix", "*");
        var sourceDisplay = request.Payload.GetValueOrDefault("sourceDisplay",
            sourcePrefix switch
            {
                "*" => "Any",
                "0.0.0.0/0" => "Internet",
                _ => sourcePrefix
            });
        var origin = Enum.Parse<NsgRuleOrigin>(request.Payload.GetValueOrDefault("origin", "Nic"));
        var protocol = Enum.Parse<NsgProtocol>(request.Payload.GetValueOrDefault("protocol", "TCP"));
        var priority = int.Parse(request.Payload.GetValueOrDefault("priority", "400"));

        await Task.Delay(400, ct).ConfigureAwait(false);

        var ruleId = $"{request.ResourceId}/rules/{name}-{Guid.NewGuid().ToString("N")[..8]}";
        Network.AddRule(request.ResourceId, new NsgSecurityRule
        {
            RuleId = ruleId,
            Name = name,
            Source = sourceDisplay,
            SourcePrefix = sourcePrefix,
            DestinationPort = port,
            Protocol = protocol,
            Action = NsgRuleAction.Allow,
            Origin = origin,
            Priority = priority
        });

        logger.LogInformation("Open port {Port} ({Origin}) on {Resource}", port, origin, request.ResourceId);
        return Guid.NewGuid().ToString("D");
    }

    public async override Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rules = await Network.GetInboundRulesAsync(request.ResourceId, ct).ConfigureAwait(false);
        var port = int.Parse(request.Payload["port"]);
        return rules.Any(r => r.DestinationPort == port && r.Action == NsgRuleAction.Allow);
    }
}

/// <summary>network.delete_rule：删除入站规则（§21 Delete Rule）。</summary>
public sealed class DeleteRuleHandler(
    MockVmNetworkService network,
    ILogger<DeleteRuleHandler> logger) : NetworkRuleHandlerBase("network.delete_rule", network, logger)
{
    protected override ImpactAssessment AssessImpact(OperationRequest request)
    {
        var ctx = Network.GetForVmAsync(request.ResourceId).GetAwaiter().GetResult();
        return SharedNsgImpact(ctx, $"delete rule '{request.Payload.GetValueOrDefault("ruleName", request.Payload["ruleId"])}'");
    }

    public async override Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var ruleId = request.Payload["ruleId"];

        await Task.Delay(400, ct).ConfigureAwait(false);

        if (!Network.TryDeleteRule(request.ResourceId, ruleId))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"Rule '{ruleId}' not found.");
        }

        logger.LogInformation("Deleted rule {Rule} on {Resource}", ruleId, request.ResourceId);
        return Guid.NewGuid().ToString("D");
    }

    public async override Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rule = await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct).ConfigureAwait(false);
        return rule is null;
    }
}
