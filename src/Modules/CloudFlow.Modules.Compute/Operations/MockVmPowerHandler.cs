using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// Mock VM 电源操作处理器：实现 Start / Restart / Power Off / Deallocate 四种 OperationType。
/// 在内存 Inventory 上执行状态迁移并模拟 Azure 延迟，用于验证 Operation Engine 全流水线。
/// </summary>
public abstract class MockVmPowerHandlerBase(
    string operationType,
    MockVmInventoryService inventory,
    ILogger logger) : IOperationHandler
{
    public string OperationType => operationType;

    /// <summary>该操作要求的前置电源状态（Restart / Power Off / Deallocate 需要 Running；Start 不限）。</summary>
    protected virtual VmPowerState? RequiredStateBefore => null;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId)
            ?? throw new OperationValidationException($"VM not found: {request.ResourceId}");

        if (RequiredStateBefore.HasValue && vm.PowerState != RequiredStateBefore.Value)
        {
            throw new OperationValidationException(
                $"VM '{vm.Name}' must be {RequiredStateBefore.Value} for {OperationType} (current: {vm.PowerState}).");
        }

        return Task.CompletedTask;
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        // 电源操作为单机操作，无共享影响面；Deallocate 由 UI 确认框 PreApproved
        return Task.FromResult(ImpactAssessment.None);
    }

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId)
            ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"VM not found: {request.ResourceId}");

        // 模拟 Azure LRO 延迟
        await Task.Delay(600, ct).ConfigureAwait(false);

        var expected = Enum.Parse<VmPowerState>(request.Payload["expectedPowerState"]);
        vm.PowerState = expected;

        var requestId = Guid.NewGuid().ToString("D");
        logger.LogInformation("Mock execute {Operation} on {Vm} → {State} (requestId {RequestId})",
            OperationType, vm.Name, expected, requestId);
        return requestId;
    }

    public Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId);
        var expected = Enum.Parse<VmPowerState>(request.Payload["expectedPowerState"]);
        return Task.FromResult(vm is not null && vm.PowerState == expected);
    }
}

public sealed class MockStartVmHandler(MockVmInventoryService inventory, ILogger<MockStartVmHandler> logger)
    : MockVmPowerHandlerBase(ComputeModule.OperationStart, inventory, logger);

public sealed class MockRestartVmHandler(MockVmInventoryService inventory, ILogger<MockRestartVmHandler> logger)
    : MockVmPowerHandlerBase(ComputeModule.OperationRestart, inventory, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;
}

public sealed class MockPowerOffVmHandler(MockVmInventoryService inventory, ILogger<MockPowerOffVmHandler> logger)
    : MockVmPowerHandlerBase(ComputeModule.OperationPowerOff, inventory, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;
}

public sealed class MockDeallocateVmHandler(MockVmInventoryService inventory, ILogger<MockDeallocateVmHandler> logger)
    : MockVmPowerHandlerBase(ComputeModule.OperationDeallocate, inventory, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;
}
