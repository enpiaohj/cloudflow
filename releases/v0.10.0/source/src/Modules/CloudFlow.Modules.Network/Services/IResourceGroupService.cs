using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 资源组页面写操作的入口：把"删除资源组"这个 UI 动作转成 <see cref="OperationRequest"/>
/// 提交 Operation Engine，与 <see cref="IVmPowerService"/>（在 Compute 模块）同一种角色划分——
/// ViewModel 不直接持有 <see cref="IOperationEngine"/>。
/// </summary>
public interface IResourceGroupService
{
    Task<OperationJob> DeleteAsync(
        string subscriptionId, string resourceGroupName, CancellationToken ct = default);
}
