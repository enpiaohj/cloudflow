using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>电源动作，与 ComputeModule 的 OperationType 一一对应。</summary>
public enum VmPowerAction
{
    Start,
    Restart,
    PowerOff,
    Deallocate
}

/// <summary>
/// VM 电源 / 规格写操作的执行器（设计文档 §19）。
///
/// 契约在 Modules，ARM 实现在 CloudFlow.Azure，App 层按请求携带的 ProviderType 路由 ——
/// 与既有 IVmDiskService（契约在 Modules）+ ArmVmDiskService（实现在 Azure）同构。
/// 这样 Handler 与 Provider 无关：同一份 Handler 既能跑 Demo 内存数据面，也能打真实 ARM。
/// </summary>
public interface IVmPowerExecutor
{
    /// <summary>读取当前电源状态；资源不存在或平台未返回时返回 null。</summary>
    Task<VmPowerState?> GetPowerStateAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>读取当前 VM 规格名（Resize 前置校验用）；读不到时返回 null。</summary>
    Task<string?> GetVmSizeAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>执行电源动作，返回 Azure Request ID（如有）。</summary>
    Task<string?> ExecuteAsync(OperationRequest request, VmPowerAction action, CancellationToken ct = default);

    /// <summary>更改规格，返回 Azure Request ID（如有）。</summary>
    Task<string?> ResizeAsync(OperationRequest request, string newSize, CancellationToken ct = default);
}
