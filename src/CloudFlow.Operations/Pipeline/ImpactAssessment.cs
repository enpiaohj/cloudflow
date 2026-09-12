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

    public static readonly ImpactAssessment None = new();
}
