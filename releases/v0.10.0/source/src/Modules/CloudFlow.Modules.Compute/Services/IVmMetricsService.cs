using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM 主机级性能指标（设计文档 §27）。只读，数据来源 Azure Monitor Metrics。
/// 指标不可用时返回 <see cref="VmMetrics.Empty"/>，不抛异常、不返回伪造值。
/// </summary>
public interface IVmMetricsService
{
    Task<VmMetrics> GetForVmAsync(string vmResourceId, CancellationToken ct = default);
}
