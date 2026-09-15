using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 数据面的电源执行器：在内存 Inventory 上做状态迁移，并模拟 Azure LRO 延迟。
/// 真实账户下绝不注册到执行链路上（由 VmPowerExecutorRouter 按 ProviderType 分流）。
/// </summary>
public sealed class MockVmPowerExecutor(
    MockVmInventoryService inventory,
    ILogger<MockVmPowerExecutor> logger) : IVmPowerExecutor
{
    public Task<VmPowerState?> GetPowerStateAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(inventory.FindById(request.ResourceId)?.PowerState);

    public Task<string?> GetVmSizeAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(inventory.FindById(request.ResourceId)?.VmSize);

    public async Task<string?> ExecuteAsync(
        OperationRequest request, VmPowerAction action, CancellationToken ct = default)
    {
        var vm = Require(request);

        // 模拟 Azure LRO 延迟
        await Task.Delay(600, ct).ConfigureAwait(false);

        vm.PowerState = action switch
        {
            VmPowerAction.Start or VmPowerAction.Restart => VmPowerState.Running,
            VmPowerAction.PowerOff => VmPowerState.Stopped,
            VmPowerAction.Deallocate => VmPowerState.Deallocated,
            _ => vm.PowerState
        };

        var requestId = Guid.NewGuid().ToString("D");
        logger.LogInformation("Mock power {Action} on {Vm} → {State} (requestId {RequestId})",
            action, vm.Name, vm.PowerState, requestId);
        return requestId;
    }

    public async Task<string?> ResizeAsync(
        OperationRequest request, string newSize, CancellationToken ct = default)
    {
        var vm = Require(request);

        await Task.Delay(800, ct).ConfigureAwait(false);
        vm.VmSize = newSize;

        logger.LogInformation("Mock resize {Vm} → {Size}", vm.Name, vm.VmSize);
        return Guid.NewGuid().ToString("D");
    }

    private VmSummary Require(OperationRequest request) =>
        inventory.FindById(request.ResourceId)
        ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
            $"未找到虚拟机：{request.ResourceId}");
}
