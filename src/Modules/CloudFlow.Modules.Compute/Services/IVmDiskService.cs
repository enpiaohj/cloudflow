using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM 磁盘查询与快照数据面（设计文档 §26）。
/// 快照的创建必须经 Operation Engine（disk.snapshot），本接口只提供数据访问。
/// P1 真实实现走 ARM Disks / Snapshots API；当前 Mock。
/// </summary>
public interface IVmDiskService
{
    /// <summary>获取 VM 的磁盘列表（OS Disk + Data Disks）。</summary>
    Task<IReadOnlyList<VmDiskInfo>> GetDisksAsync(string vmResourceId, CancellationToken ct = default);

    /// <summary>VM 快照总数。</summary>
    Task<int> GetSnapshotCountAsync(string vmResourceId, CancellationToken ct = default);

}
