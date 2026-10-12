using System.Reflection;
using Azure.ResourceManager.Monitor.Models;
using CloudFlow.Azure.Monitoring;
using CloudFlow.Modules.Compute.Models;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Monitoring;
public sealed class VmMetricsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T01:00:00Z");
    private static MonitorMetric Metric(string name, params MonitorMetricValue[] points) =>
        ArmMonitorModelFactory.MonitorMetric(name: ArmMonitorModelFactory.MonitorLocalizableString(value: name),
            timeseries: [ArmMonitorModelFactory.MonitorTimeSeriesElement(data: points)]);
    private static MonitorMetricValue Point(int minutes, double? average = null, double? total = null) =>
        ArmMonitorModelFactory.MonitorMetricValue(Now.AddMinutes(minutes), average: average, total: total);
    private static VmMetrics Map(params MonitorMetric[] metrics) =>
        (VmMetrics)typeof(ArmVmMetricsService).GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [metrics])!;

    [Fact] public void 缺失数据保持空值而真实零保留()
    {
        Assert.False(Map(Metric("Percentage CPU", Point(0))).HasAnyValue);
        Assert.Equal(0d, Map(Metric("Percentage CPU", Point(0, 0))).CpuPercent);
    }
    [Fact] public void 单个吞吐量数据点使用请求粒度换算()
    {
        Assert.Equal(10d, Map(Metric("Network In Total", Point(0, total: 3000))).NetworkInBytesPerSecond);
    }
    [Fact] public void 每个吞吐量使用自身粒度而不是CPU粒度()
    {
        var result = Map(Metric("Percentage CPU", Point(-5, 1), Point(0, 2)),
            Metric("Network In Total", Point(-1, total: 600), Point(0, total: 600)));
        Assert.Equal(10d, result.NetworkInBytesPerSecond);
    }
    [Fact] public void 只有磁盘使用率仍然有有效数据()
    {
        Assert.True(Map(Metric("OS Disk IOPS Consumed Percentage", Point(0, 0))).HasAnyValue);
    }
    [Fact] public void 内存从可用比例转换并不把缺失值当零()
    {
        Assert.Equal(37d, Map(Metric("Available Memory Percentage", Point(0, 63))).MemoryPercent);
        Assert.Null(Map().MemoryPercent);
    }
    [Fact] public void 末尾空数据点保留最近有效数据时间()
    {
        var result = Map(Metric("Percentage CPU", Point(-5, 0.012), Point(0)));
        Assert.Equal(0.012, result.CpuPercent);
        Assert.Equal(Now.AddMinutes(-5), result.SampledAt);
    }
    [Fact] public void 指标时间不同步保留最旧和最新有效时间()
    {
        var result = Map(Metric("Percentage CPU", Point(-10, 1)), Metric("Network In Total", Point(0, total: 0)));
        Assert.Equal(Now.AddMinutes(-10), result.OldestSampledAt);
        Assert.Equal(Now, result.SampledAt);
    }

}
