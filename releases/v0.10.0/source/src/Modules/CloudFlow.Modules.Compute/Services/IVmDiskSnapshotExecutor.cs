using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 磁盘快照的写操作执行器（设计文档 §26）。
///
/// 契约在 Modules，ARM 实现在 CloudFlow.Azure，App 层按请求携带的 ProviderType 路由。
/// 读接口 <see cref="IVmDiskService"/> 不再带写方法 —— 只读服务上挂一个
/// <c>CreateSnapshotAsync</c> 本身就是把写操作绕过 Operation Engine 的后门。
/// </summary>
public interface IVmDiskSnapshotExecutor
{
    /// <summary>为源磁盘创建快照，返回新快照的 Resource ID（Verify 用它确认快照真的存在）。</summary>
    Task<string> CreateSnapshotAsync(
        OperationRequest request, string diskId, string snapshotName, CancellationToken ct = default);

    /// <summary>确认快照存在且可用。</summary>
    Task<bool> SnapshotExistsAsync(
        OperationRequest request, string snapshotResourceId, CancellationToken ct = default);
}
