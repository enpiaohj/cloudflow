using CloudFlow.Core.Operations;

namespace CloudFlow.Operations.Pipeline;

/// <summary>
/// 操作处理器：每种 OperationType 注册一个实现，由 Operation Engine 按流水线调度。
/// Handler 只做单步逻辑，禁止在 Handler 内绕过流水线直接执行完整操作。
/// </summary>
public interface IOperationHandler
{
    /// <summary>对应的 OperationType，如 vm.restart。</summary>
    string OperationType { get; }

    /// <summary>Validate：参数与前置状态校验，失败抛 OperationValidationException。</summary>
    Task ValidateAsync(OperationRequest request, CancellationToken ct);

    /// <summary>Impact Analysis：判定是否需要审批、影响范围。</summary>
    Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct);

    /// <summary>Execute：执行写操作，返回 Azure Request ID（如有）。</summary>
    Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct);

    /// <summary>
    /// 带子步骤进度上报的 Execute 重载。默认实现直接转发到不带进度的旧签名——
    /// 绝大多数 Handler（电源操作、Resize、网络规则）的 Execute 只有一次 ARM 调用，
    /// 没有值得上报的中间步骤，不需要覆写这个重载，行为与之前完全一样。
    /// 只有 Execute 内部真的有多个可辨识子步骤的 Handler（创建/删除虚拟机）才覆写它，
    /// 通过 <paramref name="reportProgress"/> 把 <see cref="OperationJob.ProgressNote"/>
    /// 更新出去（复用既有的 <see cref="IJobStore.JobChanged"/> 广播，不新增事件类型）。
    /// </summary>
    Task<string?> ExecuteAsync(
        OperationRequest request, Func<string, CancellationToken, Task> reportProgress, CancellationToken ct) =>
        ExecuteAsync(request, ct);

    /// <summary>Verify：确认操作达到预期效果；没有 Verify 不算完成（设计文档 §45）。</summary>
    Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct);
}
