using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Operations.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// Operation Engine 流水线测试（设计文档 §29）：
/// Validate → Impact Analysis → Permission → Execute → Verify → Audit。
/// </summary>
public class OperationEngineTests
{
    private static OperationRequest Request(string operationType = "test.op", bool preApproved = false) => new()
    {
        OperationType = operationType,
        AccountId = "acc-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/providers/test/resources/r1",
        Risk = RiskLevel.Low,
        PreApproved = preApproved
    };

    private static (OperationEngine engine, InMemoryJobStore store, RecordingAudit audit) Build(params IOperationHandler[] handlers)
    {
        var store = new InMemoryJobStore();
        var audit = new RecordingAudit();
        var engine = new OperationEngine(handlers, store, audit, NullLogger<OperationEngine>.Instance);
        return (engine, store, audit);
    }

    // ==== 测试用记录型 Handler ====

    private sealed class RecordingHandler : IOperationHandler
    {
        public List<string> Steps { get; } = [];

        public string OperationType { get; init; } = "test.op";

        public bool ThrowOnValidate { get; init; }

        public bool RequireApproval { get; init; }

        public bool VerifyResult { get; init; } = true;

        public Task ValidateAsync(OperationRequest request, CancellationToken ct)
        {
            Steps.Add("validate");
            if (ThrowOnValidate)
            {
                throw new OperationValidationException("validation failed");
            }
            return Task.CompletedTask;
        }

        public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
        {
            Steps.Add("impact");
            return Task.FromResult(RequireApproval
                ? new ImpactAssessment { RequiresApproval = true, Description = "high impact" }
                : ImpactAssessment.None);
        }

        public Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
        {
            Steps.Add("execute");
            return Task.FromResult<string?>("req-123");
        }

        public Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
        {
            Steps.Add("verify");
            return Task.FromResult(VerifyResult);
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditRecord> Records { get; } = [];

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuditRecord> Query(string? resourceId = null, int max = 200) =>
            [.. Records.Take(max)];
    }

    // ==== 测试 ====

    [Fact]
    public async Task SubmitAsync_成功路径_按流水线顺序执行并审计()
    {
        var handler = new RecordingHandler();
        var (engine, _, audit) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.Equal(
            ["validate", "impact", "execute", "verify"],
            handler.Steps);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("req-123", job.RequestId);
        Assert.NotNull(job.CompletedAt);
        Assert.Single(audit.Records);
        Assert.Equal("Succeeded", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_校验失败_不执行不验证但写审计()
    {
        var handler = new RecordingHandler { ThrowOnValidate = true };
        var (engine, _, audit) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.Equal(["validate"], handler.Steps);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("validation failed", job.Error);
        Assert.Single(audit.Records);
        Assert.Equal("Failed", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_Verify失败_任务标记Failed()
    {
        var handler = new RecordingHandler { VerifyResult = false };
        var (engine, _, audit) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.NotNull(job.Error);
        Assert.Equal("Failed", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_需要审批_停在WaitingApproval且不执行()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, audit) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.Equal(["validate", "impact"], handler.Steps);
        Assert.Equal(JobStatus.WaitingApproval, job.Status);
        Assert.Equal("WaitingApproval", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_需要审批但已PreApproved_直接执行()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request(preApproved: true));

        Assert.Contains("execute", handler.Steps);
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task ApproveAsync_审批通过_恢复执行到完成()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, _) = Build(handler);

        var pending = await engine.SubmitAsync(Request());
        Assert.Equal(JobStatus.WaitingApproval, pending.Status);

        var final = await engine.ApproveAsync(pending.JobId);

        Assert.Equal(JobStatus.Succeeded, final.Status);
        Assert.Contains("execute", handler.Steps);
        Assert.Contains("verify", handler.Steps);
    }

    [Fact]
    public async Task ApproveAsync_未知Job_抛出异常()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, _) = Build(handler);

        await Assert.ThrowsAsync<CloudFlowException>(
            () => engine.ApproveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SubmitAsync_未注册的OperationType_任务Failed()
    {
        var (engine, _, audit) = Build(new RecordingHandler());

        var job = await engine.SubmitAsync(Request("unknown.op"));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("No handler", job.Error);
        Assert.Equal("Failed", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_Job更新事件_每次状态变化触发()
    {
        var handler = new RecordingHandler();
        var (engine, _, _) = Build(handler);

        var updates = 0;
        engine.JobUpdated += (_, _) => updates++;
        await engine.SubmitAsync(Request());

        // Validating → AnalyzingImpact → Running → WaitingAzure → Verifying → Succeeded
        Assert.True(updates >= 5);
    }
}
