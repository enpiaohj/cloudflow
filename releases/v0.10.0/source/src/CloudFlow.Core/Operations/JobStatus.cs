namespace CloudFlow.Core.Operations;

/// <summary>
/// Operation Job 状态机（设计文档 §30）。
/// </summary>
public enum JobStatus
{
    Pending,

    Validating,

    AnalyzingImpact,

    /// <summary>等待用户审批（Impact Analysis 判定需要审批时）。</summary>
    WaitingApproval,

    Running,

    /// <summary>等待 Azure 侧完成（LRO）。</summary>
    WaitingAzure,

    Verifying,

    Succeeded,

    Failed,

    Canceled
}
