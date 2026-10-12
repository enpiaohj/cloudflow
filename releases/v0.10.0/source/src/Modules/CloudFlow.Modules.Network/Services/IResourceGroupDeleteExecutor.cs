using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 资源组删除操作的执行器。
///
/// 契约在 Modules、ARM 实现在 CloudFlow.Azure、App 层按请求携带的 ProviderType 路由——
/// 与 <see cref="IVmDeleteExecutor"/>（Compute 模块）同构。
/// </summary>
public interface IResourceGroupDeleteExecutor
{
    /// <summary>
    /// 读回资源组里<b>当前真实存在</b>的资源（用于 Impact 分析——删除资源组是级联删除，
    /// 影响面必须现查，不能像删虚拟机那样枚举固定几类）。
    /// </summary>
    Task<IReadOnlyList<ResourceSummary>> GetContainedResourcesAsync(
        OperationRequest request, CancellationToken ct = default);

    /// <summary>删除资源组本身（级联删除组内全部资源），返回 Azure Request ID（如有）。</summary>
    Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>资源组是否还存在（404 视为不存在）。Verify 用。</summary>
    Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default);
}
