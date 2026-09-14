using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// Demo 模式的单资源删除执行器：演示数据面上的非虚拟机资源（网卡/OS 盘/虚拟网络等）都是
/// 按 VM 合成出来的展示项，没有独立实体可删——与 <see cref="MockVmDeleteExecutor.DeleteLinkedAsync"/>
/// 同一个理由，"删除"总是成功，Verify 视为已消失。
/// </summary>
public sealed class MockResourceDeleteExecutor : IResourceDeleteExecutor
{
    public Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult<string?>(Guid.NewGuid().ToString());

    public Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(false);
}
