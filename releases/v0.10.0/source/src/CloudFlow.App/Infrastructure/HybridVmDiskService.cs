using CloudFlow.Azure.Compute;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 混合 VM 磁盘服务：
/// - 未登录 → Mock 演示数据（概念图 2 的 WEB01 磁盘）
/// - 已登录 → ARM 真实磁盘（VM storageProfile + ARG 快照计数）
///
/// 写路径（CreateSnapshotAsync）始终交给真实实现：它只接受经 Operation Engine 的调用，
/// Mock 返回 true 的"假成功"不能出现在真实账户下。
/// </summary>
public sealed class HybridVmDiskService : IVmDiskService
{
    private readonly MockVmDiskService _mock;
    private readonly ArmVmDiskService _real;
    private readonly ScopeContext _scopeContext;

    public HybridVmDiskService(
        MockVmDiskService mock,
        ArmVmDiskService real,
        ScopeContext scopeContext)
    {
        _mock = mock;
        _real = real;
        _scopeContext = scopeContext;
    }

    public Task<IReadOnlyList<VmDiskInfo>> GetDisksAsync(
        string vmResourceId, CancellationToken ct = default) =>
        _scopeContext.ActiveAccount is null
            ? _mock.GetDisksAsync(vmResourceId, ct)
            : _real.GetDisksAsync(vmResourceId, ct);

    public Task<int> GetSnapshotCountAsync(string vmResourceId, CancellationToken ct = default) =>
        _scopeContext.ActiveAccount is null
            ? _mock.GetSnapshotCountAsync(vmResourceId, ct)
            : _real.GetSnapshotCountAsync(vmResourceId, ct);
}
