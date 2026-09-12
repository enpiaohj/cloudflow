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
            display: $"Start VM {vm.Name}",
            risk: RiskLevel.Low,
            ct: ct);

    public Task<OperationJob> RestartAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationRestart, vm,
            expected: VmPowerState.Running,
            display: $"Restart VM {vm.Name}",
            risk: RiskLevel.Low,
            ct: ct);

    public Task<OperationJob> PowerOffAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationPowerOff, vm,
            expected: VmPowerState.Stopped,
            display: $"Shut down VM {vm.Name}",
            risk: RiskLevel.Medium,
            ct: ct);

    public Task<OperationJob> DeallocateAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationDeallocate, vm,
            expected: VmPowerState.Deallocated,
            display: $"Deallocate VM {vm.Name}",
            risk: RiskLevel.Medium,
            preApproved: true, // UI 弹确认框后 PreApproved；Impact 阶段仍做影响判定
            ct: ct);

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
