using CloudFlow.Azure.Monitoring;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 混合 VM 指标服务：
/// - 未登录 → Mock 演示读数（Demo 模式，与 VM 列表 / 磁盘 / 网络同一约定），UI 明确标注为演示
/// - 已登录 → Azure Monitor Metrics 真实主机指标
///
/// 关键：登录后绝不回退 Mock —— 性能页显示的是"这台机器现在多忙"，
/// 用演示数值冒充真实读数比显示 "—" 危险得多。真实读取失败时如实抛错并展示错误。
/// </summary>
public sealed class HybridVmMetricsService : IVmMetricsService
{
    private readonly MockVmMetricsService _mock;
    private readonly ArmVmMetricsService _real;
    private readonly ScopeContext _scopeContext;

    public HybridVmMetricsService(
        MockVmMetricsService mock,
        ArmVmMetricsService real,
        ScopeContext scopeContext)
    {
        _mock = mock;
        _real = real;
        _scopeContext = scopeContext;
    }

    public Task<VmMetrics> GetForVmAsync(string vmResourceId, CancellationToken ct = default) =>
        _scopeContext.ActiveAccount is null
            ? _mock.GetForVmAsync(vmResourceId, ct)
            : _real.GetForVmAsync(vmResourceId, ct);
}
