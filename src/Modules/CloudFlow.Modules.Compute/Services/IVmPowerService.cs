using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM 电源操作（设计文档 §19）。
/// 全部操作经 Operation Engine，形成 Job（Start / Restart / Power Off / Deallocate）。
/// </summary>
public interface IVmPowerService
{
    Task<OperationJob> StartAsync(VmSummary vm, CancellationToken ct = default);

    Task<OperationJob> RestartAsync(VmSummary vm, CancellationToken ct = default);

    /// <summary>Power Off：关机但保留计算资源，费用可能继续（UI 必须与 Deallocate 区分）。</summary>
    Task<OperationJob> PowerOffAsync(VmSummary vm, CancellationToken ct = default);

    /// <summary>Deallocate：释放计算资源，停止计算计费。</summary>
    Task<OperationJob> DeallocateAsync(VmSummary vm, CancellationToken ct = default);

    /// <summary>Resize：更改 VM 规格（设计文档 §19 P1 Compute Operations）。</summary>
    Task<OperationJob> ResizeAsync(VmSummary vm, string newSize, CancellationToken ct = default);

    /// <summary>
    /// Delete：删除虚拟机（设计文档 v3.1 §87）。
    /// <paramref name="linkedResourcesToDelete"/> 是用户勾选要<b>连带删除</b>的类别集合，
    /// 空集合 = 只删 VM 本身（Azure 不会连带删除 NIC 与磁盘，它们会变成孤儿并继续计费）。
    /// </summary>
    /// <remarks>
    /// 审批门<b>不由本方法的 <c>risk</c> / <c>preApproved</c> 决定</b>：
    /// <c>DeleteVmHandler</c> 恒返回 <c>CannotBypass = true</c>，因此即使用户把审批档调成「关闭」，
    /// 删除照样会停下等审批。那两个参数只影响 Job 上记录的风险等级。
    /// </remarks>
    Task<OperationJob> DeleteAsync(
        VmSummary vm,
        IReadOnlyCollection<VmLinkedResourceKind> linkedResourcesToDelete,
        CancellationToken ct = default);
}
