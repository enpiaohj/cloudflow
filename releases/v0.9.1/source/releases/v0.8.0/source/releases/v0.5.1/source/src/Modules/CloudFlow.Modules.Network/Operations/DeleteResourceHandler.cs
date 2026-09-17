using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CloudFlow.Operations.Pipeline;

namespace CloudFlow.Modules.Network.Operations;

/// <summary>
/// resource.delete："所有资源"页——按 Azure Resource ID 通用删除单个资源，
/// 粒度比 <see cref="DeleteResourceGroupHandler"/> 更细：只删这一件，不连带删除整个资源组。
/// </summary>
/// <remarks>
/// <b>虚拟机类型一律拒绝</b>：虚拟机有自己专门的删除流程（<c>DeleteVmHandler</c>），会按用户选择
/// 清理挂载的网卡/磁盘/公网 IP；这条通用删除路径如果也能删虚拟机，会绕开那条流程、
/// 留下孤儿网卡/磁盘。UI 侧已经不给虚拟机行提供这个入口，这里是第二道防线。
/// </remarks>
public sealed class DeleteResourceHandler(IResourceDeleteExecutor executor) : IOperationHandler
{
    public string OperationType => GenericResourceModule.OperationDelete;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        if (!request.ResourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) ||
            !request.ResourceId.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"目标不是合法的 Azure Resource ID：{request.ResourceId}。");
        }

        if (string.Equals(
                ResourceTypeOf(request), "microsoft.compute/virtualmachines", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException("虚拟机请到「虚拟机」页删除，删除会一并清理挂载的网卡/磁盘。");
        }

        return Task.CompletedTask;
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var name = ParseResourceName(request.ResourceId) ?? request.ResourceId;
        var type = ResourceTypeOf(request);
        var typeSuffix = string.IsNullOrEmpty(type) ? "" : $"（{type}）";

        return Task.FromResult(new ImpactAssessment
        {
            RequiresApproval = true,
            CannotBypass = true,
            AffectedResources = 1,
            Description = $"删除资源 {name}{typeSuffix}，此操作不可恢复。"
        });
    }

    public Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct) =>
        executor.DeleteAsync(request, ct);

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct) =>
        !await executor.ExistsAsync(request, ct).ConfigureAwait(false);

    private static string ResourceTypeOf(OperationRequest request) =>
        request.Payload.GetValueOrDefault(GenericResourceModule.PayloadResourceType, "");

    private static string? ParseResourceName(string resourceId)
    {
        var slash = resourceId.LastIndexOf('/');
        return slash >= 0 && slash < resourceId.Length - 1 ? resourceId[(slash + 1)..] : null;
    }
}
