using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Operations.Pipeline;

/// <summary>
/// Operation Engine（设计文档 §29）—— 整个平台最核心的技术能力。
/// 所有 Azure Write Operation 必须经过：
/// Validate → Impact Analysis → Permission → Execute → Verify → Audit。
/// 禁止 Button → Azure SDK 直连。
/// </summary>
public sealed class OperationEngine : IOperationEngine
{
    private readonly IEnumerable<IOperationHandler> _handlers;
    private readonly IJobStore _jobStore;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<OperationEngine> _logger;

    /// <summary>处于 WaitingApproval 的挂起上下文（jobId → 恢复信息）。</summary>
    private readonly Dictionary<Guid, PendingApproval> _pendingApprovals = [];

    public event EventHandler<OperationJob>? JobUpdated;

    public OperationEngine(
        IEnumerable<IOperationHandler> handlers,
        IJobStore jobStore,
        IAuditLog auditLog,
        ILogger<OperationEngine> logger)
    {
        _handlers = handlers;
        _jobStore = jobStore;
        _auditLog = auditLog;
        _logger = logger;
    }

    public IReadOnlyList<OperationJob> Jobs => _jobStore.GetAll();

    public async Task<OperationJob> SubmitAsync(OperationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new OperationJob
        {
            AccountId = request.AccountId,
            TenantId = request.TenantId,
            AccountDisplayName = request.AccountDisplayName,
            SubscriptionId = request.SubscriptionId,
            ProviderType = request.ProviderType,
            ProviderProfileId = request.ProviderProfileId,
            ResourceId = request.ResourceId,
            OperationType = request.OperationType,
            Display = string.IsNullOrEmpty(request.Display) ? request.OperationType : request.Display,
            Risk = request.Risk,
            Status = JobStatus.Validating
        };

        await _jobStore.AddAsync(job, ct).ConfigureAwait(false);
        Notify(job);

        var handler = ResolveHandler(request.OperationType);
        if (handler is null)
        {
            return await FailAsync(job,
                $"No handler registered for operation '{request.OperationType}'.").ConfigureAwait(false);
        }

        return await GuardAsync(job, request.OperationType, async () =>
        {
            // ---- Validate ----
            await handler.ValidateAsync(request, ct).ConfigureAwait(false);

            // ---- Impact Analysis ----
            await SetStatusAsync(job, JobStatus.AnalyzingImpact, ct).ConfigureAwait(false);
            var impact = await handler.AnalyzeImpactAsync(request, ct).ConfigureAwait(false);

            // ---- Permission（审批）----
            // CannotBypass（§25 共享子网 NSG 影响面确认）连 PreApproved 都不认：
            // 它不是"要不要打扰用户"的偏好，是"这一改会波及别人"的告知。
            if (impact.RequiresApproval && (impact.CannotBypass || !request.PreApproved))
            {
                job.Status = JobStatus.WaitingApproval;
                job.Summary = impact.Description;
                job.ImpactAffectedResources = impact.AffectedResources;
                // 请求随 Job 一起落盘，审批才熬得过重启（Handler 不落盘：按操作类型反查即可）
                job.PendingRequest = request;
                await _jobStore.UpdateAsync(job, ct).ConfigureAwait(false);
                Notify(job);

                _pendingApprovals[job.JobId] = new PendingApproval(request, handler);
                await WriteAuditAsync(job, "WaitingApproval", null, impact.Description).ConfigureAwait(false);
                _logger.LogInformation("Job {JobId} ({Operation}) waiting approval: {Reason}",
                    job.JobId, request.OperationType, impact.Description);
                return job;
            }

            return await RunAsync(job, request, handler, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<OperationJob> ApproveAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = _jobStore.Find(jobId)
            ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"Job {jobId} not found.");

        // 内存里命中最好；没命中就回落到 Job 自己带的请求 —— 那是应用重启之后的唯一路径。
        // Handler 不进 Job：操作类型唯一决定用哪个 Handler，按类型反查即可，
        // 把一个执行体序列化进历史文件既无必要也多一份要审的东西。
        _pendingApprovals.Remove(jobId, out var pending);
        var request = pending?.Request ?? job.PendingRequest;
        var handler = pending?.Handler
            ?? (request is null ? null : ResolveHandler(request.OperationType));

        if (request is null || handler is null)
        {
            throw new CloudFlowException(CloudFlowErrorCode.Unknown,
                $"Job {jobId} is not waiting for approval.");
        }

        // 批准之后请求就没有留存的理由了，清掉再执行 ——
        // jobs.json 里不该长期躺着一份可被重放的写操作请求。
        job.PendingRequest = null;

        return await RunAsync(job, request, handler, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 作废挂起审批。与批准共用同一条清理纪律：挂起请求必须清掉，
    /// jobs.json 里不长期留存一份可被重放的写操作请求。
    /// 旧版 Job（无 PendingRequest）批准不了，但作废必须可以 —— 这是它唯一的出口。
    /// </summary>
    public async Task<OperationJob> RejectAsync(Guid jobId, string? reason = null, CancellationToken ct = default)
    {
        var job = _jobStore.Find(jobId)
            ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"Job {jobId} not found.");

        if (job.Status != JobStatus.WaitingApproval)
        {
            throw new CloudFlowException(CloudFlowErrorCode.Unknown,
                $"Job {jobId} is not waiting for approval.");
        }

        _pendingApprovals.Remove(jobId, out _);
        job.PendingRequest = null;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            job.Summary = reason;
        }

        // CompleteAsync 落盘 + 通知 + 写审计（Outcome = "Canceled"）
        return await CompleteAsync(job, JobStatus.Canceled).ConfigureAwait(false);
    }

    /// <summary>
    /// 该 Job 现在批得了吗。UI 据此决定显示「批准」还是「提交于旧版本，无法恢复审批」——
    /// 让"批准"按钮点下去才报错，等于把恢复能力的缺失推到用户操作之后才发现。
    /// </summary>
    public bool CanApprove(Guid jobId)    {
        if (_pendingApprovals.ContainsKey(jobId))
        {
            return true;
        }

        return _jobStore.Find(jobId) is { Status: JobStatus.WaitingApproval, PendingRequest: not null };
    }

    /// <summary>Execute → WaitingAzure → Verify → Audit 共同后段。</summary>
    private async Task<OperationJob> RunAsync(
        OperationJob job, OperationRequest request, IOperationHandler handler, CancellationToken ct)
    {
        return await GuardAsync(job, request.OperationType, async () =>
        {
            // ---- Execute ----
            await SetStatusAsync(job, JobStatus.Running, ct).ConfigureAwait(false);
            var requestId = await handler
                .ExecuteAsync(request, (note, progressCt) => ReportProgressAsync(job, note, progressCt), ct)
                .ConfigureAwait(false);
            job.RequestId = requestId;

            // ---- Azure 侧完成（真实实现为 LRO 轮询，Mock 直接通过）----
            await SetStatusAsync(job, JobStatus.WaitingAzure, ct).ConfigureAwait(false);

            // ---- Verify ----
            await SetStatusAsync(job, JobStatus.Verifying, ct).ConfigureAwait(false);
            var verified = await handler.VerifyAsync(request, requestId, ct).ConfigureAwait(false);

            if (!verified)
            {
                job.Error = "Verification failed.";
                return await CompleteAsync(job, JobStatus.Failed).ConfigureAwait(false);
            }

            job.Summary = $"{request.OperationType} verified.";
            return await CompleteAsync(job, JobStatus.Succeeded).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Submit/Run 两段共用的失败兜底：取消 → Canceled；已知业务异常 → 记警告；
    /// 未知异常 → 记错误。统一在这里，避免两段各写一份容易再次分叉的 try/catch。
    /// </summary>
    private async Task<OperationJob> GuardAsync(
        OperationJob job, string operationType, Func<Task<OperationJob>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(job, "Operation canceled.").ConfigureAwait(false);
        }
        catch (CloudFlowException ex)
        {
            _logger.LogWarning(ex, "Job {JobId} failed at {Operation}", job.JobId, operationType);
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} unexpected failure at {Operation}", job.JobId, operationType);
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
    }

    private IOperationHandler? ResolveHandler(string operationType) =>
        _handlers.FirstOrDefault(h =>
            string.Equals(h.OperationType, operationType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 状态流转必须真正 await 落盘，不能用 GetAwaiter().GetResult() 同步阻塞 ——
    /// 那样做既有 UI 线程死锁风险，也无法把 CancellationToken 透传给 Store。
    /// </summary>
    private async Task SetStatusAsync(OperationJob job, JobStatus status, CancellationToken ct)
    {
        job.Status = status;
        // 阶段切换时清空上一阶段留下的子步骤说明，避免"资源组就绪"这种 Create 阶段的文案
        // 残留到 Verify 阶段还显示着。
        job.ProgressNote = null;
        await _jobStore.UpdateAsync(job, ct).ConfigureAwait(false);
        Notify(job);
    }

    /// <summary>
    /// 供 <see cref="IOperationHandler"/> 的 Execute 子步骤上报进度用——只更新
    /// <see cref="OperationJob.ProgressNote"/>，不改变 <see cref="JobStatus"/>，
    /// 复用与状态切换同一条广播（<see cref="IJobStore.JobChanged"/> + 引擎自身的 <see cref="JobUpdated"/>）。
    /// </summary>
    private async Task ReportProgressAsync(OperationJob job, string note, CancellationToken ct)
    {
        job.ProgressNote = note;
        await _jobStore.UpdateAsync(job, ct).ConfigureAwait(false);
        Notify(job);
    }

    private async Task<OperationJob> CompleteAsync(OperationJob job, JobStatus finalStatus)
    {
        job.Status = finalStatus;
        job.CompletedAt = DateTimeOffset.Now;
        await _jobStore.UpdateAsync(job).ConfigureAwait(false);
        Notify(job);
        await WriteAuditAsync(job, finalStatus.ToString(), job.RequestId, job.Error ?? job.Summary).ConfigureAwait(false);
        return job;
    }

    private async Task<OperationJob> FailAsync(OperationJob job, string error)
    {
        job.Error = error;
        return await CompleteAsync(job, JobStatus.Failed).ConfigureAwait(false);
    }

    private async Task WriteAuditAsync(OperationJob job, string outcome, string? requestId, string? detail)
    {
        try
        {
            await _auditLog.WriteAsync(new AuditRecord
            {
                JobId = job.JobId,
                OperationType = job.OperationType,
                AccountId = job.AccountId,
                TenantId = job.TenantId,
                SubscriptionId = job.SubscriptionId,
                ProviderType = job.ProviderType,
                ProviderProfileId = job.ProviderProfileId,
                ResourceId = job.ResourceId,
                Outcome = outcome,
                Error = job.Error,
                Detail = detail
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 审计失败不能吞掉操作结果，但必须留下日志
            _logger.LogError(ex, "Failed to write audit record for job {JobId}", job.JobId);
        }
    }

    private void Notify(OperationJob job)
    {
        // JobChanged 由 JobStore 在 Add/Update 内触发，这里只发引擎自身事件
        JobUpdated?.Invoke(this, job);
    }

    private sealed record PendingApproval(OperationRequest Request, IOperationHandler Handler);
}
