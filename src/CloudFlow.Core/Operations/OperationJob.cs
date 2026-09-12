namespace CloudFlow.Core.Operations;

/// <summary>
/// Operation Job（设计文档 §30/§31）。
/// 每个 Azure Write Operation 都形成 Job，必须保存完整认证上下文，
/// 不能用 VM Name 作为唯一标识。
/// </summary>
public sealed class OperationJob
{
    public Guid JobId { get; init; } = Guid.NewGuid();

    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceId { get; init; }

    public required string OperationType { get; init; }

    public string Display { get; init; } = "";

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>Azure 侧 Request ID（Execute 阶段获得）。</summary>
    public string? RequestId { get; set; }

    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>结果摘要，如 "Restart accepted by Azure"。</summary>
    public string? Summary { get; set; }

    public string AccountDisplayName { get; set; } = "";
}
