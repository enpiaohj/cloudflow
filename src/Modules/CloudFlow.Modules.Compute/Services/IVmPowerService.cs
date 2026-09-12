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
}
