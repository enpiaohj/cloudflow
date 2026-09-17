using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流删除执行器：null = Demo（内存数据面），非 null = 真实 ARM。
///
/// 与 <see cref="VmPowerExecutorRouter"/> 同一思路、同一条判据：<b>这里不读 ScopeContext</b>。
/// 身份完全来自 <see cref="OperationRequest"/> —— 因此账户切换后，排队中的旧 Job
/// 仍会走它自己那份身份该走的路，不会突然打到新账户的订阅上。
/// </summary>
public sealed class VmDeleteExecutorRouter(
    MockVmDeleteExecutor mock,
    CloudFlow.Azure.Compute.ArmVmDeleteExecutor arm) : IVmDeleteExecutor
{
    private IVmDeleteExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
        OperationRequest request, CancellationToken ct = default) =>
        For(request).GetLinkedResourcesAsync(request, ct);

    public Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).DeleteVmAsync(request, ct);

    public Task<(bool Success, string? Reason)> DeleteLinkedAsync(
        OperationRequest request, VmLinkedResource resource, CancellationToken ct = default) =>
        For(request).DeleteLinkedAsync(request, resource, ct);

    public Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).VmExistsAsync(request, ct);
}
