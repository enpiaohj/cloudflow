using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Mock 磁盘服务（Demo 模式）：每台 VM 默认 OS Disk + Data Disk。
/// 接入真实 Azure 后替换为 ARM Disks / Snapshots 实现。
/// </summary>
public sealed class MockVmDiskService : IVmDiskService
{
    private readonly Dictionary<string, List<VmDiskInfo>> _disks = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    public Task<IReadOnlyList<VmDiskInfo>> GetDisksAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_disks.TryGetValue(vmResourceId, out var disks))
            {
                disks = SeedDefaults(vmResourceId);
                _disks[vmResourceId] = disks;
            }
            return Task.FromResult<IReadOnlyList<VmDiskInfo>>([.. disks]);
        }
    }

    public Task<int> GetSnapshotCountAsync(string vmResourceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var total = _disks.TryGetValue(vmResourceId, out var disks)
                ? disks.Sum(d => d.SnapshotCount)
                : 0;
            return Task.FromResult(total);
        }
    }

    // ==== 供 Operation Handler 调用的数据面操作 ====

    /// <summary>
    /// 记录一次快照。刻意不放在 <see cref="IVmDiskService"/> 上：
    /// 读服务带写方法，等于给写操作留了一条绕过 Operation Engine 的后门。
    /// </summary>
    public bool AddSnapshot(string vmResourceId, string diskId)
    {
        lock (_lock)
        {
            if (!_disks.TryGetValue(vmResourceId, out var disks))
            {
                return false;
            }
            var disk = disks.FirstOrDefault(d => string.Equals(d.DiskId, diskId, StringComparison.OrdinalIgnoreCase));
            if (disk is null)
            {
                return false;
            }
            disk.SnapshotCount++;
            return true;
        }
    }

    private static List<VmDiskInfo> SeedDefaults(string vmResourceId)
    {
        var name = vmResourceId.Split('/').LastOrDefault() ?? "vm";
        return
        [
            new VmDiskInfo
            {
                DiskId = $"{vmResourceId}/disks/{name}-osdisk",
                Name = $"{name.ToLowerInvariant()}-osdisk",
                Type = "OS Disk",
                Size = "128 GB",
                Tier = "Premium SSD (P10)"
            },
            new VmDiskInfo
            {
                DiskId = $"{vmResourceId}/disks/{name}-data01",
                Name = $"{name.ToLowerInvariant()}-data01",
                Type = "Data Disk",
                Size = "256 GB",
                Tier = "Standard SSD (E10)"
            }
        ];
    }
}
