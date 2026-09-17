namespace CloudFlow.Operations.Pipeline;

/// <summary>
/// Impact Analysis 结果（设计文档 §29）。
/// RequiresApproval = true 时 Job 停在 WaitingApproval，等待用户审批。
/// </summary>
public sealed record ImpactAssessment
{
    public bool RequiresApproval { get; init; }

    /// <summary>影响说明，如 "Shared NSG nsg-prod may affect 18 VMs"。</summary>
    public string? Description { get; init; }

    public int AffectedResources { get; init; }

    /// <summary>
    /// 该类审批**任何路径都绕不过**：请求里的 <c>PreApproved</c> 对它无效，
    /// 设置里的审批策略也管不到它。
    ///
    /// 专为设计文档 §25 的共享子网 NSG 影响面确认而设。§25 与"风险类审批"是两回事：
    /// 后者是用户可按偏好关掉的打扰，前者是"这一改动会波及同子网的其它虚拟机"这个事实的告知，
    /// 关掉它等于让用户在不被告知的情况下改到别人的机器。所以它必须由引擎自己保证，
    /// 而不是靠每个调用点记得传 PreApproved = false —— 那种约定迟早会在某个新入口上漏掉
    /// （change_port / delete_rule 两条路径就正是这么漏掉的）。
    /// </summary>
    public bool CannotBypass { get; init; }

    public static readonly ImpactAssessment None = new();
}
