using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流磁盘快照执行器：null = Demo（内存数据面），非 null = 真实 ARM。
/// </summary>
public sealed class VmDiskSnapshotExecutorRouter(
    MockVmDiskSnapshotExecutor mock,
    CloudFlow.Azure.Compute.ArmVmDiskSnapshotExecutor arm) : IVmDiskSnapshotExecutor
{
    private IVmDiskSnapshotExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<string> CreateSnapshotAsync(
        OperationRequest request, string diskId, string snapshotName, CancellationToken ct = default) =>
        For(request).CreateSnapshotAsync(request, diskId, snapshotName, ct);

    public Task<bool> SnapshotExistsAsync(
        OperationRequest request, string snapshotResourceId, CancellationToken ct = default) =>
        For(request).SnapshotExistsAsync(request, snapshotResourceId, ct);
}
