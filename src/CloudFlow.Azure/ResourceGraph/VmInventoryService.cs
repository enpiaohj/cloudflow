using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.Azure.ResourceGraph;

/// <summary>
/// 基于 Azure Resource Graph 的 VM Inventory（设计文档 §17）。
/// 原则：Resource Graph = Discovery / Inventory / Search；ARM API = 详细信息 / Write Operations。
/// 一次查询即可跨多个 Subscription 聚合，避免逐 VM GET。
///
/// KQL：
///   project name, resourceId, location, resourceGroup, subscriptionId,
///           properties.storageProfile.osDisk.osType, properties.hardwareProfile.vmSize,
///           properties.extended.instanceView.powerState
///
/// P1 实现：Azure.ResourceGraph SDK（QueryResourcesAsync），当前为占位。
/// </summary>
public sealed class ResourceGraphVmInventoryService : IVmInventoryService
{
    public Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "Resource Graph Inventory 将在 P0 Spike 验证通过后接入 Azure.ResourceGraph SDK。当前请使用 MockVmInventoryService。");
    }
}
