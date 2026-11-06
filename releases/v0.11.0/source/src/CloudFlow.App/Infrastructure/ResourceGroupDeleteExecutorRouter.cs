using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流资源组删除执行器：null = Demo（内存数据面），非 null = 真实 ARM。
/// 与 <see cref="VmDeleteExecutorRouter"/> 同一思路、同一条判据。
/// </summary>
public sealed class ResourceGroupDeleteExecutorRouter(
    MockResourceGroupDeleteExecutor mock,
    CloudFlow.Azure.Resources.ArmResourceGroupDeleteExecutor arm) : IResourceGroupDeleteExecutor
{
    private IResourceGroupDeleteExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<IReadOnlyList<ResourceSummary>> GetContainedResourcesAsync(
        OperationRequest request, CancellationToken ct = default) =>
        For(request).GetContainedResourcesAsync(request, ct);

    public Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).DeleteAsync(request, ct);

    public Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).ExistsAsync(request, ct);
}
