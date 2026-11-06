using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 单个资源的通用删除执行器：按 Azure Resource ID 删除，不关心具体类型
/// （虚拟机例外——虚拟机有自己专门的删除流程，会清理挂载的网卡/磁盘，"资源"页对虚拟机行
/// 不提供这个入口，见 <see cref="Models.ResourceSummary.IsVirtualMachine"/>）。
/// </summary>
public interface IResourceDeleteExecutor
{
    Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default);

    Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default);
}
