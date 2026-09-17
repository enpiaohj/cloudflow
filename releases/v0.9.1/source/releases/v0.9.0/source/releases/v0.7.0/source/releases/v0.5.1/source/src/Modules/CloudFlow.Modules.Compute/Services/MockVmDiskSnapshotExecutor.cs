using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 数据面的快照执行器：只在内存的磁盘计数上加一，不触碰 Azure。
/// 真实账户下绝不注册到执行链路上（由 VmDiskSnapshotExecutorRouter 按 ProviderType 分流）。
/// </summary>
public sealed class MockVmDiskSnapshotExecutor(MockVmDiskService disks) : IVmDiskSnapshotExecutor
{
    public async Task<string> CreateSnapshotAsync(
        OperationRequest request, string diskId, string snapshotName, CancellationToken ct = default)
    {
        // 模拟 Azure 快照 LRO 的耗时
        await Task.Delay(500, ct).ConfigureAwait(false);

        if (!disks.AddSnapshot(request.ResourceId, diskId))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound, $"未找到磁盘：{diskId}");
        }

        return $"{diskId}/snapshots/{snapshotName}";
    }

    public Task<bool> SnapshotExistsAsync(
        OperationRequest request, string snapshotResourceId, CancellationToken ct = default) =>
        Task.FromResult(true);
}
