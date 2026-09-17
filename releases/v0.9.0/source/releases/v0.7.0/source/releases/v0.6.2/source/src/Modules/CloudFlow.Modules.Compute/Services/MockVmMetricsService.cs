using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Mock 指标服务（Demo 模式，未登录 Azure 时使用）。
/// 与 MockVmInventoryService / MockVmDiskService 同一约定：只服务于 UI 开发与演示，
/// 登录真实账户后由 HybridVmMetricsService 切换到 Azure Monitor —— 演示数值绝不进入真实账户视图。
/// </summary>
public sealed class MockVmMetricsService : IVmMetricsService
{
    /// <summary>与概念图 2 的性能标签页一致的演示读数。</summary>
    private static readonly VmMetrics Demo = new()
    {
        CpuPercent = 12,
        MemoryPercent = 37,
        OsDiskIopsPercent = 6,
        OsDiskBandwidthPercent = 4,
        VmCachedIopsPercent = 3,
        VmCachedBandwidthPercent = 2,
        VmUncachedIopsPercent = 5,
        VmUncachedBandwidthPercent = 4,
        NetworkInBytesPerSecond = 1.2 * 1024 * 1024 / 8,
        NetworkOutBytesPerSecond = 0.8 * 1024 * 1024 / 8,
        DiskReadBytesPerSecond = 2.1 * 1024 * 1024,
        DiskWriteBytesPerSecond = 1.4 * 1024 * 1024,
        SampledAt = DateTimeOffset.Now,
        Interval = TimeSpan.FromMinutes(5)
    };

    public Task<VmMetrics> GetForVmAsync(string vmResourceId, CancellationToken ct = default) =>
        Task.FromResult(Demo);
}
