namespace CloudFlow.Core.Operations;

/// <summary>
/// Operation Engine 平台接口（设计文档 §29）。
/// 流水线：Validate → Impact Analysis → Permission → Execute → Verify → Audit。
/// UI / Automation / AI 都从这里走。
/// </summary>
public interface IOperationEngine
{
    /// <summary>Job 状态变化通知（UI 订阅刷新 Jobs Center）。</summary>
    event EventHandler<OperationJob>? JobUpdated;

    /// <summary>提交操作请求，返回完整走完流水线（或停在审批）的 Job。</summary>
    Task<OperationJob> SubmitAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>审批通过处于 WaitingApproval 的 Job（P1 简化：单步审批）。</summary>
    Task<OperationJob> ApproveAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// 作废处于 WaitingApproval 的 Job：状态置已取消、清掉挂起请求并写审计。
    /// 旧版本留下的无法恢复审批的 Job 也由此获得出口，否则它只能永久挂着。
    /// </summary>
    Task<OperationJob> RejectAsync(Guid jobId, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// 该 Job 现在是否可审批。重启后仍可审批的前提是 Job 里持久化了待审批请求；
    /// 旧版本留下的 WaitingApproval Job 没有这份请求，UI 要如实标为无法恢复并提供重新提交，
    /// 而不是让用户点了「批准」才发现批不了。
    /// </summary>
    bool CanApprove(Guid jobId);

    /// <summary>当前全部 Job（内存视图，历史持久化由 Data 层负责）。</summary>
    IReadOnlyList<OperationJob> Jobs { get; }
}
