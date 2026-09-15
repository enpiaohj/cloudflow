using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Network.Operations;

/// <summary>
/// Port Manager 操作处理器：Change Port / Open Port / Delete Rule。
/// 执行过程遵循设计文档 §22：读取规则 → Validate → Impact Analysis → Update → Verify → Audit。
///
/// 与 Provider 无关：读走 <see cref="IVmNetworkService"/>，写走 <see cref="IVmNetworkRuleExecutor"/>，
/// 具体是 Demo 内存数据面还是真实 NSG 由执行器实现决定。
/// </summary>
public abstract class NetworkRuleHandlerBase(
    string operationType,
    IVmNetworkService network,
    IVmNetworkRuleExecutor executor,
    ISubnetVmCounter subnetCounter,
    ILogger logger) : IOperationHandler
{
    protected const int MinPort = 1;
    protected const int MaxPort = 65535;
    protected const int MinPriority = 100;
    protected const int MaxPriority = 4096;

    public string OperationType => operationType;

    protected IVmNetworkService Network { get; } = network;

    protected IVmNetworkRuleExecutor Executor { get; } = executor;

    protected ILogger Logger { get; } = logger;

    public virtual Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        if (!request.Payload.TryGetValue("ruleId", out var ruleId) || string.IsNullOrEmpty(ruleId))
        {
            throw new OperationValidationException("缺少必需参数：ruleId。");
        }

        if (NsgRuleIds.Split(ruleId) is null)
        {
            throw new OperationValidationException(
                $"ruleId 不是合法的 NSG 规则 Resource ID（应形如 …/networkSecurityGroups/{{nsg}}/securityRules/{{name}}）：{ruleId}");
        }

        if (request.Payload.TryGetValue("port", out var portText) &&
            (!int.TryParse(portText, out var port) || port is < MinPort or > MaxPort))
        {
            throw new OperationValidationException($"端口必须在 {MinPort}–{MaxPort} 之间。");
        }

        return Task.CompletedTask;
    }

    public async Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var impact = await AssessImpactAsync(request, ct).ConfigureAwait(false);

        if (impact.RequiresApproval && !request.PreApproved)
        {
            Logger.LogWarning("Operation {Operation} requires approval: {Reason}",
                OperationType, impact.Description);
        }

        return impact;
    }

    public abstract Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct);

    public abstract Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct);

    protected abstract Task<ImpactAssessment> AssessImpactAsync(OperationRequest request, CancellationToken ct);

    /// <summary>
    /// 共享 Subnet NSG 上的变更：按设计文档 §25 必须显示影响面并等用户确认。
    ///
    /// 只有子网级规则才牵连其他 VM —— 网卡级 NSG 只挂在这一台上，
    /// 因此判据是规则自身的 Origin，而不是"这台 VM 有没有子网 NSG"。
    /// </summary>
    protected async Task<ImpactAssessment> SharedNsgImpactAsync(
        OperationRequest request,
        VmNetworkContext? context,
        NsgRuleOrigin origin,
        string description,
        CancellationToken ct)
    {
        if (origin != NsgRuleOrigin.Subnet)
        {
            return ImpactAssessment.None;
        }

        var vmCount = context?.SubnetId is { Length: > 0 } subnetId
            ? await subnetCounter.CountVmsInSubnetAsync(request, subnetId, ct).ConfigureAwait(false)
            : null;

        var scope = vmCount is { } count
            ? $"同子网共 {count} 台虚拟机都会受此变更影响"
            : "该规则位于子网级 NSG，会影响同子网的所有虚拟机";

        var nsgName = string.IsNullOrEmpty(context?.NsgName) ? "共享网络安全组" : $"共享网络安全组“{context.NsgName}”";
        var subnetName = string.IsNullOrEmpty(context?.SubnetName) ? "" : $"（子网 {context.SubnetName}）";

        return new ImpactAssessment
        {
            RequiresApproval = true,
            // §25 的影响面确认不可被 PreApproved 或设置里的审批策略绕过 ——
            // 引擎据此判定，见 OperationEngine.SubmitAsync
            CannotBypass = true,
            AffectedResources = vmCount ?? 1,
            Description = $"{nsgName}{subnetName}：{description}。{scope}。"
        };
    }

    protected async Task<VmNetworkContext?> GetContextAsync(OperationRequest request, CancellationToken ct) =>
        await Network.GetForVmAsync(request.ResourceId, ct).ConfigureAwait(false);

    /// <summary>规则 Resource ID 是写操作的唯一目标来源：拆出所属 NSG 与规则名。</summary>
    protected static (string NsgId, string RuleName) RequireTarget(OperationRequest request) =>
        NsgRuleIds.Split(request.Payload["ruleId"])
        ?? throw new CloudFlowException(CloudFlowErrorCode.Unknown,
            $"规则的 Resource ID 无法解析出所属 NSG：{request.Payload["ruleId"]}");
}

/// <summary>network.change_port：只改端口，其他字段保持不变（§22）。</summary>
public sealed class ChangePortHandler(
    IVmNetworkService network,
    IVmNetworkRuleExecutor executor,
    ISubnetVmCounter subnetCounter,
    ILogger<ChangePortHandler> logger)
    : NetworkRuleHandlerBase("network.change_port", network, executor, subnetCounter, logger)
{
    protected override async Task<ImpactAssessment> AssessImpactAsync(
        OperationRequest request, CancellationToken ct)
    {
        var context = await GetContextAsync(request, ct).ConfigureAwait(false);
        var rule = await FindRuleAsync(request, ct).ConfigureAwait(false);
        var name = rule?.Name ?? request.Payload.GetValueOrDefault("ruleName", request.Payload["ruleId"]);

        return await SharedNsgImpactAsync(
            request, context, rule?.Origin ?? NsgRuleOrigin.Nic, $"将规则“{name}”的端口改为 {request.Payload.GetValueOrDefault("port")}", ct)
            .ConfigureAwait(false);
    }

    public override async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var (nsgId, ruleName) = RequireTarget(request);
        var newPort = int.Parse(request.Payload["port"]);

        return await Executor.ChangePortAsync(request, nsgId, ruleName, newPort, ct).ConfigureAwait(false);
    }

    public override async Task<bool> VerifyAsync(
        OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rule = await FindRuleAsync(request, ct).ConfigureAwait(false);
        return rule is not null && rule.DestinationPort == int.Parse(request.Payload["port"]);
    }

    private Task<NsgSecurityRule?> FindRuleAsync(OperationRequest request, CancellationToken ct) =>
        Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct);
}

/// <summary>
/// network.open_port：在指定 NSG 上新建入站 / 出站安全规则（§23）。
/// 尽管操作名和历史上一直叫"打开端口"，实际能创建 Allow 也能创建 Deny——
/// 跟 Azure 门户自己的"新建规则"面板一致，不是只能开放、不能拒绝。
/// </summary>
public sealed class OpenPortHandler(
    IVmNetworkService network,
    IVmNetworkRuleExecutor executor,
    ISubnetVmCounter subnetCounter,
    ILogger<OpenPortHandler> logger)
    : NetworkRuleHandlerBase("network.open_port", network, executor, subnetCounter, logger)
{
    /// <summary>
    /// 用户在对话框里填的那个对端地址所在的 payload 键。
    /// 入站是来源、出站是目标 —— 两侧各有一个键，由方向决定读哪一个。
    /// </summary>
    private static string PeerPrefixKey(NsgRuleDirection direction) =>
        direction == NsgRuleDirection.Outbound ? "destinationPrefix" : "sourcePrefix";

    private static string PeerDisplayKey(NsgRuleDirection direction) =>
        direction == NsgRuleDirection.Outbound ? "destinationDisplay" : "sourceDisplay";

    public override Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        // open_port 的目标是"要往哪个 NSG 里加规则"，没有既有规则可解析，必须由 UI 明确给出。
        if (!request.Payload.TryGetValue("nsgId", out var nsgId) || string.IsNullOrWhiteSpace(nsgId))
        {
            throw new OperationValidationException("缺少必需参数：nsgId（目标网络安全组）。");
        }

        if (!request.Payload.TryGetValue("port", out var portText) ||
            !int.TryParse(portText, out var port) || port is < MinPort or > MaxPort)
        {
            throw new OperationValidationException($"端口必须在 {MinPort}–{MaxPort} 之间。");
        }

        if (!request.Payload.TryGetValue("ruleName", out var ruleName) || string.IsNullOrWhiteSpace(ruleName))
        {
            throw new OperationValidationException("缺少必需参数：ruleName。");
        }

        if (request.Payload.TryGetValue("priority", out var priorityText) &&
            (!int.TryParse(priorityText, out var priority) || priority is < MinPriority or > MaxPriority))
        {
            throw new OperationValidationException($"优先级必须在 {MinPriority}–{MaxPriority} 之间。");
        }

        // 方向写错会让规则落到相反的方向：不认识的取值直接拒绝，不猜成入站
        if (request.Payload.TryGetValue("direction", out var directionText) &&
            !Enum.TryParse<NsgRuleDirection>(directionText, ignoreCase: true, out _))
        {
            throw new OperationValidationException(
                $"direction 必须是 {nameof(NsgRuleDirection.Inbound)} 或 {nameof(NsgRuleDirection.Outbound)}。");
        }

        // Allow/Deny 写错会创建出一条方向相反的规则（该放行的被拒绝、该拒绝的被放行），
        // 不认识的取值直接拒绝，不默默按 Allow 处理。
        if (request.Payload.TryGetValue("action", out var actionText) &&
            !Enum.TryParse<NsgRuleAction>(actionText, ignoreCase: true, out _))
        {
            throw new OperationValidationException(
                $"action 必须是 {nameof(NsgRuleAction.Allow)} 或 {nameof(NsgRuleAction.Deny)}。");
        }

        return Task.CompletedTask;
    }

    protected override async Task<ImpactAssessment> AssessImpactAsync(
        OperationRequest request, CancellationToken ct)
    {
        var context = await GetContextAsync(request, ct).ConfigureAwait(false);
        var origin = OriginOf(request);
        var direction = DirectionOf(request);
        var action = ActionOf(request);
        var peer = request.Payload.GetValueOrDefault(PeerPrefixKey(direction), "*");
        var port = request.Payload.GetValueOrDefault("port");
        var isOutbound = direction == NsgRuleDirection.Outbound;
        var isDeny = action == NsgRuleAction.Deny;

        var shared = await SharedNsgImpactAsync(
            request, context, origin,
            isDeny
                ? (isOutbound ? $"新增出站规则拒绝端口 {port}" : $"新增规则拒绝端口 {port}")
                : (isOutbound ? $"新增出站规则开放端口 {port}" : $"新增规则开放端口 {port}"),
            ct).ConfigureAwait(false);
        if (shared.RequiresApproval)
        {
            return shared;
        }

        // 对端是"任意"都要审批，但 Allow / Deny 的风险是两回事，措辞不能套同一句：
        // Allow + 任意是"新增暴露面"（入站是"整个 Internet 能访问到"，出站是"这台机器
        // 能访问任意目标"）；Deny + 任意反而是"新增一条可能连带挡住其他规则的封堵"——
        // 该不该批必须看这条规则实际生效后是"开了什么"还是"堵了什么"，不能不分青红皂白
        // 都说成"暴露"，那对 Deny 规则是说反的。
        if (!IsInternet(peer))
        {
            return ImpactAssessment.None;
        }

        var description = isDeny
            ? (isOutbound
                ? "该出站规则将拒绝这台虚拟机访问任意目标；若优先级低于某条放行规则，可能连带挡住原本该放行的流量。"
                : $"该规则将拒绝任意来源访问端口 {port}；若优先级低于某条放行规则，可能连带挡住原本该放行的流量。")
            : (isOutbound
                ? "该出站规则的目标是任意地址（含 Internet），将显式放行这台虚拟机的所有出站流量。"
                : $"开放端口 {port} 将对 Internet 暴露该服务。");

        return new ImpactAssessment
        {
            RequiresApproval = true,
            AffectedResources = 1,
            Description = description
        };
    }

    public override async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var direction = DirectionOf(request);
        var isOutbound = direction == NsgRuleDirection.Outbound;

        // 用户填的对端只放在它该在的那一侧；另一侧固定 "*"
        // （Azure 对入站规则的目标、出站规则的来源都是这个默认值）。
        var peerPrefix = request.Payload.GetValueOrDefault(PeerPrefixKey(direction), "*");
        var peerDisplay = request.Payload.GetValueOrDefault(
            PeerDisplayKey(direction), DescribePeer(direction, peerPrefix));

        var draft = new NsgRuleDraft
        {
            Name = request.Payload["ruleName"],
            Port = int.Parse(request.Payload["port"]),
            Protocol = Enum.TryParse<NsgProtocol>(request.Payload.GetValueOrDefault("protocol", "TCP"), out var protocol)
                ? protocol
                : NsgProtocol.TCP,
            Action = ActionOf(request),
            SourcePrefix = isOutbound ? "*" : peerPrefix,
            SourceDisplay = isOutbound ? "Any" : peerDisplay,
            Priority = int.TryParse(request.Payload.GetValueOrDefault("priority"), out var priority) ? priority : 400,
            Direction = direction,
            DestinationPrefix = isOutbound ? peerPrefix : "*",
            DestinationDisplay = isOutbound ? peerDisplay : null
        };

        return await Executor.OpenPortAsync(request, request.Payload["nsgId"], draft, ct).ConfigureAwait(false);
    }

    public override async Task<bool> VerifyAsync(
        OperationRequest request, string? requestId, CancellationToken ct)
    {
        // 校验必须查**规则实际会落到的那张表**：拿入站列表去验出站规则永远为 false，
        // 会让一次成功的写操作被判定成失败。
        var direction = DirectionOf(request);
        var rules = direction == NsgRuleDirection.Outbound
            ? await Network.GetOutboundRulesAsync(request.ResourceId, ct).ConfigureAwait(false)
            : await Network.GetInboundRulesAsync(request.ResourceId, ct).ConfigureAwait(false);

        var port = int.Parse(request.Payload["port"]);
        var name = request.Payload["ruleName"];

        return rules.Any(r =>
            r.Direction == direction &&
            r.DestinationPort == port &&
            r.Action == NsgRuleAction.Allow &&
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInternet(string peerPrefix) =>
        peerPrefix is "*" or "0.0.0.0/0" or "Internet";

    /// <summary>缺 display 时的兜底文案。同一句 "Any" 在两侧含义不同，所以按方向分别给。</summary>
    private static string DescribePeer(NsgRuleDirection direction, string prefix) => prefix switch
    {
        "*" => direction == NsgRuleDirection.Outbound ? "任意目标 (Any)" : "Any",
        "0.0.0.0/0" => "Internet",
        var other => other
    };

    private static NsgRuleDirection DirectionOf(OperationRequest request) =>
        Enum.TryParse<NsgRuleDirection>(
            request.Payload.GetValueOrDefault("direction", nameof(NsgRuleDirection.Inbound)),
            ignoreCase: true,
            out var direction)
            ? direction
            : NsgRuleDirection.Inbound;

    private static NsgRuleOrigin OriginOf(OperationRequest request) =>
        Enum.TryParse<NsgRuleOrigin>(request.Payload.GetValueOrDefault("origin", "Nic"), out var origin)
            ? origin
            : NsgRuleOrigin.Nic;

    /// <summary>缺省 Allow——沿用这个字段加入之前唯一支持的行为，不让老 payload 突然变成 Deny。</summary>
    private static NsgRuleAction ActionOf(OperationRequest request) =>
        Enum.TryParse<NsgRuleAction>(
            request.Payload.GetValueOrDefault("action", nameof(NsgRuleAction.Allow)),
            ignoreCase: true,
            out var action)
            ? action
            : NsgRuleAction.Allow;
}

/// <summary>network.delete_rule：删除入站 / 出站规则（§21 Delete Rule）。</summary>
public sealed class DeleteRuleHandler(
    IVmNetworkService network,
    IVmNetworkRuleExecutor executor,
    ISubnetVmCounter subnetCounter,
    ILogger<DeleteRuleHandler> logger)
    : NetworkRuleHandlerBase("network.delete_rule", network, executor, subnetCounter, logger)
{
    protected override async Task<ImpactAssessment> AssessImpactAsync(
        OperationRequest request, CancellationToken ct)
    {
        var context = await GetContextAsync(request, ct).ConfigureAwait(false);
        var rule = await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct)
            .ConfigureAwait(false);
        var name = rule?.Name ?? request.Payload.GetValueOrDefault("ruleName", request.Payload["ruleId"]);

        return await SharedNsgImpactAsync(
            request, context, rule?.Origin ?? NsgRuleOrigin.Nic, $"删除规则“{name}”", ct)
            .ConfigureAwait(false);
    }

    public override async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var (nsgId, ruleName) = RequireTarget(request);

        return await Executor.DeleteRuleAsync(request, nsgId, ruleName, ct).ConfigureAwait(false);
    }

    public override async Task<bool> VerifyAsync(
        OperationRequest request, string? requestId, CancellationToken ct)
    {
        var rule = await Network.FindRuleAsync(request.ResourceId, request.Payload["ruleId"], ct)
            .ConfigureAwait(false);
        return rule is null;
    }
}
