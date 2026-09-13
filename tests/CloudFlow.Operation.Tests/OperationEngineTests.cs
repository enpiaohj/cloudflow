using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
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
        ProviderType = AuthenticationProviderType.EntraMsal,
        ProviderProfileId = "profile-1",
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

        /// <summary>模拟 §25 的共享子网 NSG 影响面确认（任何路径都绕不过）。</summary>
        public bool CannotBypass { get; init; }

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
                ? new ImpactAssessment
                {
                    RequiresApproval = true,
                    CannotBypass = CannotBypass,
                    Description = "high impact"
                }
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
        Assert.Equal(AuthenticationProviderType.EntraMsal, job.ProviderType);
        Assert.Equal("profile-1", job.ProviderProfileId);
        Assert.Equal("req-123", job.RequestId);
        Assert.NotNull(job.CompletedAt);
        Assert.Single(audit.Records);
        Assert.Equal(AuthenticationProviderType.EntraMsal, audit.Records[0].ProviderType);
        Assert.Equal("profile-1", audit.Records[0].ProviderProfileId);
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

    [Fact]
    public async Task SubmitAsync_账户可读名称写入Job()
    {
        // 「任务」页的账户列直接绑 Job.AccountDisplayName。引擎若漏拷，历史记录会永远显示 "—"，
        // 审计上无法回答"这条操作是谁执行的"。
        var handler = new RecordingHandler();
        var (engine, _, _) = Build(handler);

        var request = Request();
        var job = await engine.SubmitAsync(new OperationRequest
        {
            OperationType = request.OperationType,
            AccountId = request.AccountId,
            TenantId = request.TenantId,
            AccountDisplayName = "Piao Hongji",
            SubscriptionId = request.SubscriptionId,
            ResourceId = request.ResourceId
        });

        Assert.Equal("Piao Hongji", job.AccountDisplayName);
    }

    // ==== §25：不可绕过的审批（设计文档 §25）====

    [Fact]
    public async Task SubmitAsync_不可绕过的审批_即使PreApproved也停在WaitingApproval()
    {
        // 这是本轮最关键的一条：共享子网 NSG 的影响面确认必须弹。
        // 此前 change_port / delete_rule 两条路径硬编码 PreApproved = true，
        // 与 §25 相遇时审批门根本不触发，用户在不被告知"同子网还有多少台机器"的情况下改了规则。
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };
        var (engine, _, audit) = Build(handler);

        var job = await engine.SubmitAsync(Request(preApproved: true));

        Assert.Equal(JobStatus.WaitingApproval, job.Status);
        Assert.DoesNotContain("execute", handler.Steps);
        Assert.Equal("WaitingApproval", audit.Records[0].Outcome);
    }

    [Fact]
    public async Task SubmitAsync_普通审批_PreApproved仍然放行()
    {
        // 不可绕过只针对 CannotBypass 那一类，不能顺手把所有 PreApproved 都作废 ——
        // 解除分配 / 改规格靠它避免"对话框问一遍、审批再问一遍"
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = false };
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request(preApproved: true));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("execute", handler.Steps);
    }

    [Fact]
    public async Task SubmitAsync_不可绕过的审批_记下了待审批请求()
    {
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.NotNull(job.PendingRequest);
        Assert.Equal(job.OperationType, job.PendingRequest!.OperationType);
    }

    [Fact]
    public async Task SubmitAsync_直接执行的操作_不留下待审批请求()
    {
        var handler = new RecordingHandler();
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        // 没有挂起就没有什么要恢复；留下请求等于把一份可重放的写操作留在历史里
        Assert.Null(job.PendingRequest);
    }

    // ==== 跨重启恢复审批 ====

    [Fact]
    public async Task ApproveAsync_换一个引擎实例_仍能凭Job里的请求恢复执行()
    {
        // 模拟重启：内存里的 _pendingApprovals 随进程消失，只剩同一个 JobStore 里的 Job。
        // 这条不成立的话，重启后 WaitingApproval 的 Job 就永久卡死 —— 既批不了也执行不了。
        var store = new InMemoryJobStore();
        var audit = new RecordingAudit();
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };

        var first = new OperationEngine([handler], store, audit, NullLogger<OperationEngine>.Instance);
        var pending = await first.SubmitAsync(Request());
        Assert.Equal(JobStatus.WaitingApproval, pending.Status);

        var restarted = new OperationEngine([handler], store, audit, NullLogger<OperationEngine>.Instance);
        var final = await restarted.ApproveAsync(pending.JobId);

        Assert.Equal(JobStatus.Succeeded, final.Status);
        Assert.Contains("execute", handler.Steps);
        Assert.Contains("verify", handler.Steps);
    }

    [Fact]
    public async Task ApproveAsync_批准后清空待审批请求()
    {
        var store = new InMemoryJobStore();
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };
        var engine = new OperationEngine([handler], store, new RecordingAudit(),
            NullLogger<OperationEngine>.Instance);

        var pending = await engine.SubmitAsync(Request());
        await engine.ApproveAsync(pending.JobId);

        // 执行完还留着请求，等于在 jobs.json 里长期存着一份可被重放的写操作
        Assert.Null(store.Find(pending.JobId)!.PendingRequest);
    }

    [Fact]
    public async Task ApproveAsync_旧版本留下的Job_内存与请求都没有时抛出()
    {
        // 旧版 jobs.json 里的 WaitingApproval Job 没有 PendingRequest，
        // 这类 Job 恢复不了。UI 靠 CanApprove 提前拦住，这里保证引擎自己也不会悄悄放行。
        var store = new InMemoryJobStore();
        var legacy = new OperationJob
        {
            AccountId = "acc-1",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ResourceId = "/subscriptions/sub-1/providers/test/resources/r1",
            OperationType = "test.op",
            Status = JobStatus.WaitingApproval
        };
        await store.AddAsync(legacy);

        var engine = new OperationEngine([new RecordingHandler()], store, new RecordingAudit(),
            NullLogger<OperationEngine>.Instance);

        Assert.False(engine.CanApprove(legacy.JobId));
        await Assert.ThrowsAsync<CloudFlowException>(() => engine.ApproveAsync(legacy.JobId));
    }

    [Fact]
    public async Task CanApprove_当次会话挂起的Job为真()
    {
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };
        var (engine, _, _) = Build(handler);

        var pending = await engine.SubmitAsync(Request());

        Assert.True(engine.CanApprove(pending.JobId));
    }

    [Fact]
    public async Task CanApprove_执行完成的Job为假()
    {
        var handler = new RecordingHandler();
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        Assert.False(engine.CanApprove(job.JobId));
    }

    // ==== RejectAsync：挂起审批的作废出口 ====
    //
    // 没有它，一个用户不想继续的挂起 Job 只能永久挂着；
    // 旧版 jobs.json 里「无法恢复审批」的 Job 更是连批准的路都没有。

    [Fact]
    public async Task RejectAsync_等待审批的Job_置已取消且不执行写审计()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, audit) = Build(handler);

        var pending = await engine.SubmitAsync(Request());
        var rejected = await engine.RejectAsync(pending.JobId);

        Assert.Equal(JobStatus.Canceled, rejected.Status);
        Assert.Equal(JobStatus.Canceled, store_status(engine, pending.JobId));
        // 作废意味着"到 validate/impact 为止"，绝不能再往下执行
        Assert.DoesNotContain("execute", handler.Steps);
        Assert.Contains(audit.Records, r => r.JobId == pending.JobId && r.Outcome == "Canceled");
    }

    [Fact]
    public async Task RejectAsync_作废后清空待审批请求()
    {
        var handler = new RecordingHandler { RequireApproval = true, CannotBypass = true };
        var (engine, store, _) = Build(handler);

        var pending = await engine.SubmitAsync(Request());
        await engine.RejectAsync(pending.JobId);

        Assert.Null(store.Find(pending.JobId)!.PendingRequest);
        // 作废后的 Job 也不再可审批
        Assert.False(engine.CanApprove(pending.JobId));
    }

    [Fact]
    public async Task RejectAsync_旧版本留下的Job_也能作废获得出口()
    {
        // 旧版 Job 批准不了（无 PendingRequest），但作废必须可以 —— 否则永久挂着。
        var store = new InMemoryJobStore();
        var audit = new RecordingAudit();
        var legacy = new OperationJob
        {
            AccountId = "acc-1",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ResourceId = "/subscriptions/sub-1/providers/test/resources/r1",
            OperationType = "test.op",
            Status = JobStatus.WaitingApproval
        };
        await store.AddAsync(legacy);

        var engine = new OperationEngine([new RecordingHandler()], store, audit,
            NullLogger<OperationEngine>.Instance);

        var rejected = await engine.RejectAsync(legacy.JobId);

        Assert.Equal(JobStatus.Canceled, rejected.Status);
        Assert.Contains(audit.Records, r => r.JobId == legacy.JobId && r.Outcome == "Canceled");
    }

    [Fact]
    public async Task RejectAsync_作废原因写入Job()
    {
        var handler = new RecordingHandler { RequireApproval = true };
        var (engine, _, _) = Build(handler);

        var pending = await engine.SubmitAsync(Request());
        var rejected = await engine.RejectAsync(pending.JobId, "不再需要此操作");

        Assert.Contains("不再需要此操作", rejected.Summary);
    }

    [Fact]
    public async Task RejectAsync_非等待审批状态的Job_抛出()
    {
        var handler = new RecordingHandler();
        var (engine, _, _) = Build(handler);

        var job = await engine.SubmitAsync(Request());

        await Assert.ThrowsAsync<CloudFlowException>(() => engine.RejectAsync(job.JobId));
    }

    private static JobStatus store_status(OperationEngine engine, Guid jobId) =>
        engine.Jobs.First(j => j.JobId == jobId).Status;

    [Fact]
    public async Task 内存JobStore清空后GetAll为空()
    {
        var store = new InMemoryJobStore();
        await store.AddAsync(StoredJob("vm.start", "启动"));
        await store.AddAsync(StoredJob("vm.restart", "重启"));

        await store.ClearAsync();

        Assert.Empty(store.GetAll());
    }

    private static OperationJob StoredJob(string operationType, string display) => new()
    {
        AccountId = "acc-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
        OperationType = operationType,
        Display = display
    };
}
