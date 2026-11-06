using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// disk.snapshot：为指定磁盘创建快照（设计文档 §26，P1 Exit Gate 项）。
///
/// 与 Provider 无关：走 <see cref="IVmDiskSnapshotExecutor"/>，
/// Demo 内存数据面与真实 ARM 由执行器实现决定。
/// </summary>
public sealed class SnapshotVmHandler(
    IVmDiskService disks,
    IVmDiskSnapshotExecutor executor,
    ILogger<SnapshotVmHandler> logger) : IOperationHandler
{
    public string OperationType => ComputeModule.OperationSnapshot;

    public async Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var diskId = request.Payload.GetValueOrDefault("diskId", "");
        if (string.IsNullOrWhiteSpace(diskId))
        {
            throw new OperationValidationException("缺少必需参数：diskId。");
        }

        // 快照名必须由提交方给定并随请求固化：执行时现编一个，
        // Verify 阶段就没有稳定的名字去找它，"已验证"也就无从谈起。
        if (string.IsNullOrWhiteSpace(request.Payload.GetValueOrDefault("snapshotName", "")))
        {
            throw new OperationValidationException("缺少必需参数：snapshotName。");
        }

        var vmDisks = await disks.GetDisksAsync(request.ResourceId, ct).ConfigureAwait(false);
        if (!vmDisks.Any(d => string.Equals(d.DiskId, diskId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new OperationValidationException($"磁盘不属于该虚拟机：{diskId}");
        }
    }

    /// <summary>快照是低风险的增量操作，不改变磁盘本身，因此不设审批门。</summary>
    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(ImpactAssessment.None);

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var diskId = request.Payload["diskId"];
        var snapshotName = request.Payload["snapshotName"];

        var snapshotId = await executor
            .CreateSnapshotAsync(request, diskId, snapshotName, ct)
            .ConfigureAwait(false);

        logger.LogInformation("快照已提交：{Disk} → {Snapshot}", diskId, snapshotId);
        return snapshotId;
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        // requestId 是执行器返回的快照 Resource ID；为空说明 Execute 没走完，不能算通过。
        if (string.IsNullOrEmpty(requestId))
        {
            return false;
        }

        return await executor.SnapshotExistsAsync(request, requestId, ct).ConfigureAwait(false);
    }
}
