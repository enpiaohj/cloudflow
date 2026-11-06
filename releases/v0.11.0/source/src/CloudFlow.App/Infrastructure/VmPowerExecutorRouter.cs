using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流电源执行器：null = Demo（内存数据面），非 null = 真实 ARM。
///
/// 与 HybridVmInventoryService 同一思路，但判据更严格：这里不读 ScopeContext。
/// 身份完全来自 OperationRequest —— 因此账户切换后，排队中的旧 Job 仍会走它自己那份身份该走的路，
/// 不会突然打到新账户的订阅上。
/// </summary>
public sealed class VmPowerExecutorRouter(
    MockVmPowerExecutor mock,
    CloudFlow.Azure.Compute.ArmVmPowerExecutor arm) : IVmPowerExecutor
{
    private IVmPowerExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<VmPowerState?> GetPowerStateAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).GetPowerStateAsync(request, ct);

    public Task<string?> GetVmSizeAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).GetVmSizeAsync(request, ct);

    public Task<string?> ExecuteAsync(OperationRequest request, VmPowerAction action, CancellationToken ct = default) =>
        For(request).ExecuteAsync(request, action, ct);

    public Task<string?> ResizeAsync(OperationRequest request, string newSize, CancellationToken ct = default) =>
        For(request).ResizeAsync(request, newSize, ct);
}
