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

    /// <summary>当前全部 Job（内存视图，历史持久化由 Data 层负责）。</summary>
    IReadOnlyList<OperationJob> Jobs { get; }
}
