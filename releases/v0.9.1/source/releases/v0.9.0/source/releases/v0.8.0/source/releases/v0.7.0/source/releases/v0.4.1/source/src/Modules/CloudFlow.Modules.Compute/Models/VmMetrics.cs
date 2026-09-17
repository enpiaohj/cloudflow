namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// VM 主机级指标快照（设计文档 §27）。
/// 数据来源：Azure Monitor Metrics 的 Host 指标（无需 Guest 诊断扩展）。
///
/// 所有数值字段可空：指标不可用（VM 已解除分配、平台未产生数据点）时为 null，
/// UI 显示 "—" —— 不能用 0 或默认值填充，那会被误读为“负载为零”。
/// </summary>
public sealed class VmMetrics
{
    /// <summary>CPU 使用率（%）：取聚合窗口内所有采样点的平均值。</summary>
    public double? CpuPercent { get; init; }

    /// <summary>
    /// 内存使用率（%）。Azure 只提供「可用内存百分比」，此处已换算为使用率（100 − 可用），
    /// 与 CPU 列口径一致，避免用户把「可用 63%」误读成「用了 63%」。
    /// 无需 Guest 诊断扩展（Host 指标）。
    /// </summary>
    public double? MemoryPercent { get; init; }

    /// <summary>OS 磁盘 IOPS 消耗百分比（相对该规格的 IOPS 上限）。</summary>
    public double? OsDiskIopsPercent { get; init; }

    /// <summary>OS 磁盘带宽消耗百分比（相对该规格的带宽上限）。</summary>
    public double? OsDiskBandwidthPercent { get; init; }

    /// <summary>缓存 IOPS 消耗百分比。</summary>
    public double? VmCachedIopsPercent { get; init; }

    /// <summary>缓存带宽消耗百分比。</summary>
    public double? VmCachedBandwidthPercent { get; init; }

    /// <summary>非缓存 IOPS 消耗百分比。</summary>
    public double? VmUncachedIopsPercent { get; init; }

    /// <summary>非缓存带宽消耗百分比。</summary>
    public double? VmUncachedBandwidthPercent { get; init; }

    public double? NetworkInBytesPerSecond { get; init; }

    public double? NetworkOutBytesPerSecond { get; init; }

    public double? DiskReadBytesPerSecond { get; init; }

    public double? DiskWriteBytesPerSecond { get; init; }

    /// <summary>
    /// 最后一个数据点的时间。UI 必须展示它 —— 指标有分钟级延迟，
    /// 不标时间会让用户把这些值当成此刻的实时读数。
    /// </summary>
    public DateTimeOffset? SampledAt { get; init; }

    /// <summary>聚合窗口（与 Azure Monitor 请求的 interval 一致）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>是否拿到任何有效数据点。</summary>
    public bool HasAnyValue =>
        CpuPercent is not null ||
        MemoryPercent is not null ||
        NetworkInBytesPerSecond is not null ||
        NetworkOutBytesPerSecond is not null ||
        DiskReadBytesPerSecond is not null ||
        DiskWriteBytesPerSecond is not null;

    /// <summary>指标完全不可用时的空对象。</summary>
    public static VmMetrics Empty { get; } = new();
}
