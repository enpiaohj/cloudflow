using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM Inventory 查询（设计文档 §38：所有 Module 统一接受 ResourceScope，
/// 禁止 GetVirtualMachines(subscriptionId) 式签名）。
/// P1 真实实现走 Azure Resource Graph；当前 Mock。
/// </summary>
public interface IVmInventoryService
{
    Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default);
}
