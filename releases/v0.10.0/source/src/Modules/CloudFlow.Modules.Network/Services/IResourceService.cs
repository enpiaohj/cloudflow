using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// "所有资源"页写操作的入口：把"删除单个资源"这个 UI 动作转成 <see cref="OperationRequest"/>
/// 提交 Operation Engine，与 <see cref="IResourceGroupService"/> 同一种角色划分。
/// </summary>
public interface IResourceService
{
    /// <param name="resourceType">Azure 资源类型（如 Microsoft.Network/networkInterfaces），
    /// 只用于 Impact 描述可读性与 Handler 端拦截虚拟机类型，不参与鉴权。</param>
    Task<OperationJob> DeleteAsync(
        string subscriptionId, string resourceId, string resourceName, string resourceType,
        CancellationToken ct = default);
}
