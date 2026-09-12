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
            SubscriptionId = request.SubscriptionId,
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

        try
        {
            // ---- Validate ----
            await handler.ValidateAsync(request, ct).ConfigureAwait(false);

            // ---- Impact Analysis ----
            SetStatus(job, JobStatus.AnalyzingImpact);
            var impact = await handler.AnalyzeImpactAsync(request, ct).ConfigureAwait(false);

            // ---- Permission（审批）----
            if (impact.RequiresApproval && !request.PreApproved)
            {
                job.Status = JobStatus.WaitingApproval;
                job.Summary = impact.Description;
                await _jobStore.UpdateAsync(job, ct).ConfigureAwait(false);
                Notify(job);

                _pendingApprovals[job.JobId] = new PendingApproval(request, handler);
                await WriteAuditAsync(job, "WaitingApproval", null, impact.Description).ConfigureAwait(false);
                _logger.LogInformation("Job {JobId} ({Operation}) waiting approval: {Reason}",
                    job.JobId, request.OperationType, impact.Description);
                return job;
            }

            return await RunAsync(job, request, handler, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(job, "Operation canceled.").ConfigureAwait(false);
        }
        catch (CloudFlowException ex)
        {
            _logger.LogWarning(ex, "Job {JobId} failed at {Operation}", job.JobId, request.OperationType);
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} unexpected failure at {Operation}", job.JobId, request.OperationType);
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
    }

    public async Task<OperationJob> ApproveAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_pendingApprovals.Remove(jobId, out var pending))
        {
            throw new CloudFlowException(CloudFlowErrorCode.Unknown,
                $"Job {jobId} is not waiting for approval.");
        }

        var job = _jobStore.Find(jobId)
            ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"Job {jobId} not found.");

        return await RunAsync(job, pending.Request, pending.Handler, ct).ConfigureAwait(false);
    }

    /// <summary>Execute → WaitingAzure → Verify → Audit 共同后段。</summary>
    private async Task<OperationJob> RunAsync(
        OperationJob job, OperationRequest request, IOperationHandler handler, CancellationToken ct)
    {
        try
        {
            // ---- Execute ----
            SetStatus(job, JobStatus.Running);
            var requestId = await handler.ExecuteAsync(request, ct).ConfigureAwait(false);
            job.RequestId = requestId;

            // ---- Azure 侧完成（真实实现为 LRO 轮询，Mock 直接通过）----
            SetStatus(job, JobStatus.WaitingAzure);

            // ---- Verify ----
            SetStatus(job, JobStatus.Verifying);
            var verified = await handler.VerifyAsync(request, requestId, ct).ConfigureAwait(false);

            if (!verified)
            {
                job.Error = "Verification failed.";
                return await CompleteAsync(job, JobStatus.Failed).ConfigureAwait(false);
            }

            job.Summary = $"{request.OperationType} verified.";
            return await CompleteAsync(job, JobStatus.Succeeded).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(job, "Operation canceled.").ConfigureAwait(false);
        }
        catch (CloudFlowException ex)
        {
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed during execution", job.JobId);
            return await FailAsync(job, ex.Message).ConfigureAwait(false);
        }
    }

    private IOperationHandler? ResolveHandler(string operationType) =>
        _handlers.FirstOrDefault(h =>
            string.Equals(h.OperationType, operationType, StringComparison.OrdinalIgnoreCase));

    private void SetStatus(OperationJob job, JobStatus status)
    {
        job.Status = status;
        _jobStore.UpdateAsync(job).GetAwaiter().GetResult();
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
