using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 模式的删除执行器：在内存演示数据面上<b>真的</b>把 VM 移掉。
/// </summary>
/// <remarks>
/// <para>
/// <b>演示数据面没有真实的附着关系</b>，这里按 ResourceId 约定合成一张网卡与一块 OS 盘
/// （公网 IP 只在 <see cref="VmSummary.PublicIp"/> 非空时才有）——
/// 与 <see cref="MockVmInventoryService"/> 合成 PrivateIp 的做法同源：
/// 在演示数据的世界里，那些资源本来就是这样推出来的。
/// </para>
/// <para>
/// 只在未登录（Demo）时注册；真实账户下由 <c>ArmVmDeleteExecutor</c> 接手，判据见 App 层的路由器。
/// </para>
/// </remarks>
public sealed class MockVmDeleteExecutor(MockVmInventoryService inventory) : IVmDeleteExecutor
{
    /// <summary>模拟 LRO 的耗时，与 <see cref="MockVmPowerExecutor"/> 保持一致。</summary>
    private const int SimulatedLatencyMs = 600;

    public Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
        OperationRequest request, CancellationToken ct = default)
    {
        var vm = inventory.FindById(request.ResourceId);
        if (vm is null)
        {
            return Task.FromResult<IReadOnlyList<VmLinkedResource>>([]);
        }

        var name = VmNameOf(vm);
        var prefix = $"/subscriptions/{vm.SubscriptionId}/resourceGroups/{vm.ResourceGroupName}/providers";

        List<VmLinkedResource> linked =
        [
            new($"{prefix}/Microsoft.Network/networkInterfaces/{name}-nic",
                VmLinkedResourceKind.NetworkInterface, $"{name}-nic（网卡）"),
            new($"{prefix}/Microsoft.Compute/disks/{name}-osdisk",
                VmLinkedResourceKind.OsDisk, $"{name}-osdisk（OS 磁盘）")
        ];

        if (!string.IsNullOrWhiteSpace(vm.PublicIp))
        {
            linked.Add(new($"{prefix}/Microsoft.Network/publicIPAddresses/{name}-ip",
                VmLinkedResourceKind.PublicIpAddress, $"{name}-ip（公网 IP）"));
        }

        return Task.FromResult<IReadOnlyList<VmLinkedResource>>(linked);
    }

    public async Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default)
    {
        // 模拟 LRO。真实 ARM 路径下这个等待由 SDK 的 WaitUntil.Completed 承担，
        // 这里用延时表达"这是一次异步长操作"。
        await Task.Delay(SimulatedLatencyMs, ct).ConfigureAwait(false);

        return inventory.Remove(request.ResourceId) ? Guid.NewGuid().ToString() : null;
    }

    /// <summary>
    /// 演示数据面上连带资源<b>没有实体</b>（它们由 <see cref="GetLinkedResourcesAsync"/> 按需合成），
    /// 因此"删除"总是成功 —— 这不是"忘了删"，而是它们本来就不存在。
    /// 一致性由这一点保证：VM 一旦移出，<see cref="GetLinkedResourcesAsync"/> 就返回空列表，
    /// Verify 的"连带资源已消失"自然成立。真实路径见 <c>ArmVmDeleteExecutor</c>。
    /// </summary>
    public Task<(bool Success, string? Reason)> DeleteLinkedAsync(
        OperationRequest request, VmLinkedResource resource, CancellationToken ct = default) =>
        Task.FromResult<(bool, string?)>((true, null));

    public Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(inventory.FindById(request.ResourceId) is not null);

    /// <summary>Resource ID 末段即 VM 名称（Azure Resource ID 是唯一主键）。</summary>
    private static string VmNameOf(VmSummary vm)
    {
        var slash = vm.ResourceId.LastIndexOf('/');
        return slash >= 0 && slash < vm.ResourceId.Length - 1 ? vm.ResourceId[(slash + 1)..] : vm.Name;
    }
}
