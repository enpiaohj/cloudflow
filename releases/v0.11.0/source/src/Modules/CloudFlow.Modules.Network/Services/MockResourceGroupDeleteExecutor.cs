using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Demo 模式的资源组删除执行器：对内存演示数据面做同等操作——组内的"资源"就是这个资源组下
/// 演示出来的虚拟机（含它们各自的网卡/OS 盘，跟 <see cref="MockVmDeleteExecutor"/> 合成连带
/// 资源同一个思路），删除资源组等于把这些虚拟机从演示数据里一并移除。
/// </summary>
public sealed class MockResourceGroupDeleteExecutor(MockVmInventoryService inventory) : IResourceGroupDeleteExecutor
{
    public async Task<IReadOnlyList<ResourceSummary>> GetContainedResourcesAsync(
        OperationRequest request, CancellationToken ct = default)
    {
        var rgName = ResourceGroupNameOf(request.ResourceId);
        var vms = await VmsInGroupAsync(rgName, ct).ConfigureAwait(false);

        var result = new List<ResourceSummary>();
        foreach (var vm in vms)
        {
            var prefix = $"/subscriptions/{vm.SubscriptionId}/resourceGroups/{rgName}/providers";
            result.Add(new ResourceSummary(
                vm.Name, "Microsoft.Compute/virtualMachines", vm.ResourceId, vm.Region));
            result.Add(new ResourceSummary(
                $"{vm.Name}-nic", "Microsoft.Network/networkInterfaces",
                $"{prefix}/Microsoft.Network/networkInterfaces/{vm.Name}-nic", vm.Region));
            result.Add(new ResourceSummary(
                $"{vm.Name}-osdisk", "Microsoft.Compute/disks",
                $"{prefix}/Microsoft.Compute/disks/{vm.Name}-osdisk", vm.Region));
        }

        return result;
    }

    public async Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default)
    {
        var rgName = ResourceGroupNameOf(request.ResourceId);
        var vms = await VmsInGroupAsync(rgName, ct).ConfigureAwait(false);
        foreach (var vm in vms)
        {
            inventory.Remove(vm.ResourceId);
        }

        return Guid.NewGuid().ToString();
    }

    /// <summary>
    /// 演示数据面没有独立的"资源组"实体，只能靠"这个组下还有没有虚拟机"反推是否还存在——
    /// <see cref="DeleteAsync"/> 会把组内虚拟机全部移出，之后这里查到 0 条即视为资源组已消失。
    /// </summary>
    public async Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default)
    {
        var rgName = ResourceGroupNameOf(request.ResourceId);
        return !string.IsNullOrEmpty(rgName) &&
               (await VmsInGroupAsync(rgName, ct).ConfigureAwait(false)).Count > 0;
    }

    private async Task<IReadOnlyList<Modules.Compute.Models.VmSummary>> VmsInGroupAsync(
        string rgName, CancellationToken ct)
    {
        var all = await inventory.QueryAsync(new ResourceScope(), ct).ConfigureAwait(false);
        return [.. all.Where(vm => string.Equals(vm.ResourceGroupName, rgName, StringComparison.OrdinalIgnoreCase))];
    }

    private static string ResourceGroupNameOf(string resourceId)
    {
        var segments = resourceId.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return "";
    }
}
