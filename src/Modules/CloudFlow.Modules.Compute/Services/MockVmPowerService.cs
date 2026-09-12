using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Mock VM 电源操作：把 UI 动作转成结构化 OperationRequest 提交 Operation Engine。
/// 即使在 Demo 模式，操作也走完整流水线（Validate → Impact → Execute → Verify → Audit）。
/// </summary>
public sealed class MockVmPowerService(IOperationEngine engine, MockAccountContext account) : IVmPowerService
{
    public Task<OperationJob> StartAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationStart, vm,
            expected: VmPowerState.Running,
            display: $"启动虚拟机 {vm.Name}",
            risk: RiskLevel.Low,
            ct: ct);

    public Task<OperationJob> RestartAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationRestart, vm,
            expected: VmPowerState.Running,
            display: $"重启虚拟机 {vm.Name}",
            risk: RiskLevel.Low,
            ct: ct);

    public Task<OperationJob> PowerOffAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationPowerOff, vm,
            expected: VmPowerState.Stopped,
            display: $"关机 {vm.Name}",
            risk: RiskLevel.Medium,
            ct: ct);

    public Task<OperationJob> DeallocateAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationDeallocate, vm,
            expected: VmPowerState.Deallocated,
            display: $"解除分配 {vm.Name}",
            risk: RiskLevel.Medium,
            preApproved: true, // UI 弹确认框后 PreApproved；Impact 阶段仍做影响判定
            ct: ct);

    public Task<OperationJob> ResizeAsync(VmSummary vm, string newSize, CancellationToken ct = default) =>
        engine.SubmitAsync(new OperationRequest
        {
            OperationType = ComputeModule.OperationResize,
            AccountId = account.AccountId,
            TenantId = account.TenantId,
            SubscriptionId = vm.SubscriptionId,
            ResourceId = vm.ResourceId,
            Risk = RiskLevel.Medium,
            PreApproved = true, // UI 对话框确认后提交
            Display = $"更改规格 {vm.Name} → {newSize}",
            Payload = new Dictionary<string, string>
            {
                ["newSize"] = newSize
            }
        }, ct);

    private Task<OperationJob> SubmitAsync(
        string operationType, VmSummary vm, VmPowerState expected,
        string display, RiskLevel risk, bool preApproved = false, CancellationToken ct = default) =>
        engine.SubmitAsync(new OperationRequest
        {
            OperationType = operationType,
            AccountId = account.AccountId,
            TenantId = account.TenantId,
            SubscriptionId = vm.SubscriptionId,
            ResourceId = vm.ResourceId,
            Risk = risk,
            PreApproved = preApproved,
            Display = display,
            Payload = new Dictionary<string, string>
            {
                ["expectedPowerState"] = expected.ToString()
            }
        }, ct);
}
