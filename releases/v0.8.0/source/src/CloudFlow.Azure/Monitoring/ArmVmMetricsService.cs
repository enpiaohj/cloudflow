using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Monitor;
using Azure.ResourceManager.Monitor.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Monitoring;

/// <summary>
/// Azure Monitor Metrics 实现的 VM 主机级指标读取（设计文档 §27）。
///
/// 只用 Host 指标（Percentage CPU / Network In|Out Total / Disk Read|Write Bytes）：
/// 这些无需在 VM 内启用 Guest 诊断扩展，符合“安装 CloudFlow = 完成全部环境准备”的产品原则。
/// Guest 级指标（内存、磁盘 IOPS 细分）需要诊断扩展，本版本不取。
///
/// 走 <c>Azure.ResourceManager.Monitor</c> 而不是手写 REST：它与本仓强制的
/// <see cref="IAzureClientFactory"/> → <see cref="ArmClient"/> 路径同源，用的就是 ARM 的
/// audience，不需要另配一套 <c>metrics.monitor.azure.com</c> 的授权。
///
/// **为什么不走 <c>Azure.Monitor.Query</c>**：该包已于 2025-10-16 被微软弃用。
/// 官方迁移表给出的替代里，单资源指标查询对应的是本类使用的
/// <c>Azure.ResourceManager.Monitor</c>；另一条路 <c>Azure.Monitor.Query.Metrics</c>
/// 要的是独立于 ARM 的 audience，而 <c>CallbackTokenCredential</c> 会把 scope 原样透传给
/// MSAL / 嵌入式 CLI —— 企业应用注册未必已对该 audience 获同意，失败点会落在身份层。
/// </summary>
public sealed class ArmVmMetricsService : IVmMetricsService
{
    /// <summary>查询窗口：最近 30 分钟，聚合粒度 5 分钟（6 个数据点）。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestedInterval = TimeSpan.FromMinutes(5);

    /// <summary>指标名必须与 Azure Monitor 注册名完全一致，否则整个请求 400。</summary>
    private const string CpuMetric = "Percentage CPU";
    private const string NetworkInMetric = "Network In Total";
    private const string NetworkOutMetric = "Network Out Total";
    private const string DiskReadMetric = "Disk Read Bytes";
    private const string DiskWriteMetric = "Disk Write Bytes";

    /// <summary>可用内存百分比。Azure 只有「可用」，使用率需换算为 100 − 可用。</summary>
    private const string AvailableMemoryPercentMetric = "Available Memory Percentage";

    // 以下为「消耗百分比」类指标：相对该规格上限的使用率，全部为 Host 指标，无需诊断扩展。
    private const string OsDiskIopsPercentMetric = "OS Disk IOPS Consumed Percentage";
    private const string OsDiskBandwidthPercentMetric = "OS Disk Bandwidth Consumed Percentage";
    private const string VmCachedIopsPercentMetric = "VM Cached IOPS Consumed Percentage";
    private const string VmCachedBandwidthPercentMetric = "VM Cached Bandwidth Consumed Percentage";
    private const string VmUncachedIopsPercentMetric = "VM Uncached IOPS Consumed Percentage";
    private const string VmUncachedBandwidthPercentMetric = "VM Uncached Bandwidth Consumed Percentage";

    private static readonly string[] MetricNames =
    [
        CpuMetric, NetworkInMetric, NetworkOutMetric, DiskReadMetric, DiskWriteMetric,
        AvailableMemoryPercentMetric,
        OsDiskIopsPercentMetric, OsDiskBandwidthPercentMetric,
        VmCachedIopsPercentMetric, VmCachedBandwidthPercentMetric,
        VmUncachedIopsPercentMetric, VmUncachedBandwidthPercentMetric
    ];

    private readonly IAzureClientFactory _clientFactory;
    private readonly ScopeContext _scopeContext;
    private readonly ILogger<ArmVmMetricsService> _logger;

    public ArmVmMetricsService(
        IAzureClientFactory clientFactory,
        ScopeContext scopeContext,
        ILogger<ArmVmMetricsService> logger)
    {
        _clientFactory = clientFactory;
        _scopeContext = scopeContext;
        _logger = logger;
    }

    public async Task<VmMetrics> GetForVmAsync(string vmResourceId, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(vmResourceId, ct).ConfigureAwait(false);

        var end = DateTimeOffset.UtcNow;
        var start = end - Window;

        var metrics = new List<MonitorMetric>();
        try
        {
            // SDK 把它建模成可分页集合（AsyncPageable）而不是一次性响应 —— 每个 MonitorMetric 是一项指标。
            // 响应级的元数据（Interval 等）在这个版本没有暴露（响应模型是 internal 的），
            // 所以换算速率用的粒度只能从数据点的时间戳间距反推，见 InferInterval。
            var pageable = armClient.GetMonitorMetricsAsync(
                new ResourceIdentifier(vmResourceId),
                new ArmResourceGetMonitorMetricsOptions
                {
                    Timespan = $"{start:yyyy-MM-ddTHH:mm:ssZ}/{end:yyyy-MM-ddTHH:mm:ssZ}",
                    Interval = RequestedInterval,
                    Metricnames = string.Join(",", MetricNames),
                    Aggregation = "average,total"
                },
                ct);

            await foreach (var metric in pageable.WithCancellation(ct).ConfigureAwait(false))
            {
                metrics.Add(metric);
            }
        }
        catch (RequestFailedException ex)
        {
            // SDK 抛的是 RequestFailedException；上层只认 CloudFlowException，
            // 不显式映射会让 UI 的既有降级文案与错误展示失效
            throw new CloudFlowException(CloudFlowErrorCode.AzureError,
                $"Azure Monitor 指标查询失败（{ex.Status}）：{Truncate(ex.Message)}");
        }

        var result = Map(metrics);
        _logger.LogInformation(
            "Azure Monitor 指标：{ResourceId} CPU={Cpu} 数据时间={SampledAt} 粒度={Interval}",
            vmResourceId, result.CpuPercent, result.SampledAt, result.Interval);
        return result;
    }

    /// <summary>
    /// 按资源所属订阅取凭据：租户跟着订阅走。与 <c>ArmVmNetworkService.CreateClientAsync</c> 同构。
    /// </summary>
    private async Task<ArmClient> CreateClientAsync(string vmResourceId, CancellationToken ct)
    {
        var account = _scopeContext.ActiveAccount
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");

        return await _clientFactory
            .CreateAsync(
                ActiveAccountContext.Create(account, _scopeContext, ActiveAccountContext.SubscriptionIdOf(vmResourceId)),
                ct)
            .ConfigureAwait(false);
    }

    private static VmMetrics Map(IReadOnlyList<MonitorMetric> metrics)
    {
        double? cpu = null, networkIn = null, networkOut = null, diskRead = null, diskWrite = null;
        double? availableMemoryPercent = null;
        double? osDiskIops = null, osDiskBandwidth = null;
        double? cachedIops = null, cachedBandwidth = null;
        double? uncachedIops = null, uncachedBandwidth = null;
        DateTimeOffset? sampledAt = null;
        TimeSpan? interval = null;

        foreach (var metric in metrics)
        {
            var point = LastPoint(metric);
            if (point is null)
            {
                continue;
            }

            var (timestamp, average, total) = point.Value;
            sampledAt = sampledAt is null || timestamp > sampledAt ? timestamp : sampledAt;
            interval ??= InferInterval(metric.Timeseries);

            switch (metric.Name?.Value)
            {
                // CPU 是百分比：窗口内多次采样的平均值才是有意义的读数
                case CpuMetric:
                    cpu = average;
                    break;
                // 字节类是窗口内的累计量：除以窗口秒数换算成速率，供 UI 以 KB/s、MB/s 展示
                case NetworkInMetric:
                    networkIn = PerSecond(total, interval);
                    break;
                case NetworkOutMetric:
                    networkOut = PerSecond(total, interval);
                    break;
                case DiskReadMetric:
                    diskRead = PerSecond(total, interval);
                    break;
                case DiskWriteMetric:
                    diskWrite = PerSecond(total, interval);
                    break;
                // 百分比类：同样取窗口内平均值
                case AvailableMemoryPercentMetric:
                    availableMemoryPercent = average;
                    break;
                case OsDiskIopsPercentMetric:
                    osDiskIops = average;
                    break;
                case OsDiskBandwidthPercentMetric:
                    osDiskBandwidth = average;
                    break;
                case VmCachedIopsPercentMetric:
                    cachedIops = average;
                    break;
                case VmCachedBandwidthPercentMetric:
                    cachedBandwidth = average;
                    break;
                case VmUncachedIopsPercentMetric:
                    uncachedIops = average;
                    break;
                case VmUncachedBandwidthPercentMetric:
                    uncachedBandwidth = average;
                    break;
            }
        }

        return new VmMetrics
        {
            CpuPercent = cpu,
            // Azure 给的是「可用内存占比」，这里换算成使用率，与 CPU 口径一致
            MemoryPercent = availableMemoryPercent is { } avail ? 100 - avail : null,
            OsDiskIopsPercent = osDiskIops,
            OsDiskBandwidthPercent = osDiskBandwidth,
            VmCachedIopsPercent = cachedIops,
            VmCachedBandwidthPercent = cachedBandwidth,
            VmUncachedIopsPercent = uncachedIops,
            VmUncachedBandwidthPercent = uncachedBandwidth,
            NetworkInBytesPerSecond = networkIn,
            NetworkOutBytesPerSecond = networkOut,
            DiskReadBytesPerSecond = diskRead,
            DiskWriteBytesPerSecond = diskWrite,
            SampledAt = sampledAt,
            Interval = interval ?? RequestedInterval
        };
    }

    /// <summary>
    /// 从相邻数据点的时间戳间距反推服务端实际使用的聚合粒度。
    ///
    /// 为什么不直接用请求里给的 5 分钟：服务端可以按自己的保留期与粒度调整
    /// （长窗口或较老的数据可能退化成 15 分钟），照请求值换算会让速率整体偏大或偏小。
    /// 这个版本的 SDK 没有暴露响应级的 Interval（响应模型是 internal 的），
    /// 而数据点间距本身就是服务端给出的事实，比请求值可靠。
    ///
    /// 拿不到两个数据点时返回 null，由调用方退回请求值 —— 宁可换算略有偏差，
    /// 也不能因此把一次成功的读取判成失败。
    /// </summary>
    private static TimeSpan? InferInterval(IReadOnlyList<MonitorTimeSeriesElement>? series)
    {
        var data = series?.FirstOrDefault()?.Data;
        if (data is null || data.Count < 2)
        {
            return null;
        }

        var delta = data[^1].TimeStamp - data[^2].TimeStamp;
        return delta > TimeSpan.Zero ? delta : null;
    }

    /// <summary>取时序里最后一个含数值的数据点（末尾点可能为 null，如 VM 刚停止）。</summary>
    private static (DateTimeOffset Timestamp, double? Average, double? Total)? LastPoint(MonitorMetric metric)
    {
        // 未按维度拆分时只有一条时序
        var data = metric.Timeseries?.FirstOrDefault()?.Data;
        if (data is null)
        {
            return null;
        }

        (DateTimeOffset, double?, double?)? last = null;
        foreach (var point in data)
        {
            if (point.Average is null && point.Total is null)
            {
                continue;
            }

            last = (point.TimeStamp, point.Average, point.Total);
        }

        return last;
    }

    private static double? PerSecond(double? total, TimeSpan? interval) =>
        total is null || interval is null || interval.Value.TotalSeconds <= 0
            ? null
            : total / interval.Value.TotalSeconds;

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
