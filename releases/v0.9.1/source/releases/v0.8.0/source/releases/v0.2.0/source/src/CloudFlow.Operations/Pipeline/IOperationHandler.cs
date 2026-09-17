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

    /// <summary>Verify：确认操作达到预期效果；没有 Verify 不算完成（设计文档 §45）。</summary>
    Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct);
}
