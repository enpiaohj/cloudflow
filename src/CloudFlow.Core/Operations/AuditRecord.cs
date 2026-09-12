namespace CloudFlow.Core.Operations;

/// <summary>审计记录（每次操作完成后写入，Append-Only）。</summary>
public sealed class AuditRecord
{
    public Guid RecordId { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public Guid JobId { get; init; }

    public required string OperationType { get; init; }

    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceId { get; init; }

    /// <summary>结果：Succeeded / Failed / WaitingApproval / Canceled。</summary>
    public required string Outcome { get; init; }

    public string? Error { get; init; }

    public string? Detail { get; init; }
}

/// <summary>审计日志接口（Append-Only，设计文档 §29 Audit 环节）。</summary>
public interface IAuditLog
{
    Task WriteAsync(AuditRecord record, CancellationToken ct = default);

    /// <summary>查询（按时间倒序，可按资源过滤）。</summary>
    IReadOnlyList<AuditRecord> Query(string? resourceId = null, int max = 200);
}
