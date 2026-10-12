using CloudFlow.Azure.Compute;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 混合 VM 详情服务：
/// - 未登录 → Mock 演示数据（概览页在 Demo 模式下也有内容可看）
/// - 已登录 → ARM 真实读取（<c>GET virtualMachines/{name}</c> + 规格目录 + 关闭计划）
///
/// 只读分流，没有写路径。
/// </summary>
public sealed class HybridVmDetailService(
    MockVmDetailService mock,
    ArmVmDetailService real,
    ScopeContext scopeContext) : IVmDetailService
{
    public Task<VmDetailInfo?> GetAsync(string vmResourceId, CancellationToken ct = default) =>
        scopeContext.ActiveAccount is null
            ? mock.GetAsync(vmResourceId, ct)
            : real.GetAsync(vmResourceId, ct);
}
