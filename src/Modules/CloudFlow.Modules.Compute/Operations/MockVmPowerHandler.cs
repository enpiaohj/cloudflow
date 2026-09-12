using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// Mock VM 电源操作处理器基类：Start / Restart / Power Off / Deallocate。
/// 在内存 Inventory 上执行状态迁移并模拟 Azure 延迟，用于验证 Operation Engine 全流水线。
/// </summary>
public abstract class MockVmPowerHandlerBase(
    string operationType,
    MockVmInventoryService inventory,
    ILogger logger) : IOperationHandler
{
    public string OperationType => operationType;

    /// <summary>该操作要求的前置电源状态（null 表示由子类自定义校验）。</summary>
    protected virtual VmPowerState? RequiredStateBefore => null;

    /// <summary>状态校验（子类可覆写，如 Start 允许 Stopped / Deallocated）。</summary>
    protected virtual void ValidateState(VmSummary vm)
    {
        if (RequiredStateBefore.HasValue && vm.PowerState != RequiredStateBefore.Value)
        {
            throw new OperationValidationException(
                $"虚拟机“{vm.Name}”当前为 {CfStatusText(vm.PowerState)}，无法执行 {OperationType}（要求 {CfStatusText(RequiredStateBefore.Value)}）。");
        }
    }

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId)
            ?? throw new OperationValidationException($"未找到虚拟机：{request.ResourceId}");

        ValidateState(vm);
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
                $"未找到虚拟机：{request.ResourceId}");

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

    /// <summary>中文状态文本（避免在模块层依赖 UI 转换器，此处用轻量映射）。</summary>
    protected static string CfStatusText(VmPowerState state) => state switch
    {
        VmPowerState.Running => "运行中",
        VmPowerState.Stopped => "已停止",
        VmPowerState.Deallocated => "已解除分配",
        _ => state.ToString()
    };
}

public sealed class MockStartVmHandler(MockVmInventoryService inventory, ILogger<MockStartVmHandler> logger)
    : MockVmPowerHandlerBase(ComputeModule.OperationStart, inventory, logger)
{
    /// <summary>启动允许 Stopped 与 Deallocated 两种状态（Running 状态拒绝）。</summary>
    protected override void ValidateState(VmSummary vm)
    {
        if (vm.PowerState == VmPowerState.Running)
        {
            throw new OperationValidationException($"虚拟机“{vm.Name}”已在运行中，无需启动。");
        }
    }
}

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

/// <summary>
/// vm.resize：更改 VM 规格（设计文档 §19）。UI 对话框确认后 PreApproved 提交。
/// </summary>
public sealed class MockResizeVmHandler(
    MockVmInventoryService inventory,
    ILogger<MockResizeVmHandler> logger) : IOperationHandler
{
    public static readonly string[] SupportedSizes =
    [
        "Standard_B2s",
        "Standard_B2ms",
        "Standard_B4ms",
        "Standard_D2s_v5",
        "Standard_D4s_v5",
        "Standard_E4s_v5"
    ];

    public string OperationType => ComputeModule.OperationResize;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId)
            ?? throw new OperationValidationException($"未找到虚拟机：{request.ResourceId}");

        if (!request.Payload.TryGetValue("newSize", out var newSize) ||
            !SupportedSizes.Contains(newSize, StringComparer.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"不支持的 VM 规格：{request.Payload.GetValueOrDefault("newSize")}");
        }

        if (string.Equals(vm.VmSize, newSize, StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"目标规格与当前规格相同（{newSize}）。");
        }

        return Task.CompletedTask;
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(ImpactAssessment.None);

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId)
            ?? throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound, $"未找到虚拟机：{request.ResourceId}");

        await Task.Delay(800, ct).ConfigureAwait(false);
        vm.VmSize = request.Payload["newSize"];

        logger.LogInformation("Mock resize {Vm} → {Size}", vm.Name, vm.VmSize);
        return Guid.NewGuid().ToString("D");
    }

    public Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var vm = inventory.FindById(request.ResourceId);
        return Task.FromResult(vm is not null &&
            string.Equals(vm.VmSize, request.Payload["newSize"], StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// disk.snapshot：为磁盘创建快照（设计文档 §26，P1 Exit Gate 项）。
/// 高风险操作（Detach）才需 Impact Analysis；快照为低风险增量操作。
/// </summary>
public sealed class MockSnapshotVmHandler(
    MockVmInventoryService inventory,
    IVmDiskService diskService,
    ILogger<MockSnapshotVmHandler> logger) : IOperationHandler
{
    public string OperationType => ComputeModule.OperationSnapshot;

    public async Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        _ = inventory.FindById(request.ResourceId)
            ?? throw new OperationValidationException($"未找到虚拟机：{request.ResourceId}");

        var diskId = request.Payload.GetValueOrDefault("diskId", "");
        var disks = await diskService.GetDisksAsync(request.ResourceId, ct).ConfigureAwait(false);
        if (!disks.Any(d => string.Equals(d.DiskId, diskId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new OperationValidationException($"未找到磁盘：{diskId}");
        }
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(ImpactAssessment.None);

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        await Task.Delay(500, ct).ConfigureAwait(false);

        var ok = await diskService.CreateSnapshotAsync(request.ResourceId, request.Payload["diskId"], ct)
            .ConfigureAwait(false);
        if (!ok)
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到磁盘：{request.Payload["diskId"]}");
        }

        logger.LogInformation("Mock snapshot disk {Disk} on {Resource}",
            request.Payload["diskId"], request.ResourceId);
        return Guid.NewGuid().ToString("D");
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        // Verify：快照计数在执行后保持 ≥1（Mock 数据面已在 Execute 落账）
        var count = await diskService.GetSnapshotCountAsync(request.ResourceId, ct).ConfigureAwait(false);
        return count > 0;
    }
}
