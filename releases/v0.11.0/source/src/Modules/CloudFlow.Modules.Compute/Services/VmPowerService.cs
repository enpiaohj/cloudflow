using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM 电源操作：把 UI 动作转成结构化 OperationRequest 提交 Operation Engine。
/// 即使在 Demo 模式，操作也走完整流水线（Validate → Impact → Execute → Verify → Audit）。
///
/// 本服务与 Provider 无关 —— 走 Mock 还是真实 ARM 由请求携带的 ProviderType 决定
/// （见 <see cref="OperationRequestFactory"/>），因此不再以 Mock 命名。
/// </summary>
public sealed class VmPowerService(
    IOperationEngine engine,
    OperationRequestFactory requests,
    IApprovalPolicy approvalPolicy) : IVmPowerService
{
    public Task<OperationJob> StartAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationStart, vm,
            expected: VmPowerState.Running,
            display: $"启动虚拟机 {vm.Name}",
            risk: RiskLevel.Low,
            ct: ct);

    // 风险等级不是"有多危险"的抽象评分，而是"这个动作会不会打断用户自己"：
    // High 的两个（重启 / 关机）是页头一键触发、不经任何对话框的，
    // 点错了服务就断了；Medium 的两个（解除分配 / 改规格）各自先弹了带影响说明的对话框，
    // 用户在下单前已经看过一遍。审批策略的「仅高危」档正是按这条线切。
    public Task<OperationJob> RestartAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationRestart, vm,
            expected: VmPowerState.Running,
            display: $"重启虚拟机 {vm.Name}",
            risk: RiskLevel.High,
            ct: ct);

    public Task<OperationJob> PowerOffAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationPowerOff, vm,
            expected: VmPowerState.Stopped,
            display: $"关机 {vm.Name}",
            risk: RiskLevel.High,
            ct: ct);

    public Task<OperationJob> DeallocateAsync(VmSummary vm, CancellationToken ct = default) =>
        SubmitAsync(ComputeModule.OperationDeallocate, vm,
            expected: VmPowerState.Deallocated,
            display: $"解除分配 {vm.Name}",
            risk: RiskLevel.Medium,
            ct: ct);

    public Task<OperationJob> ResizeAsync(VmSummary vm, string newSize, CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            ComputeModule.OperationResize,
            vm.SubscriptionId,
            vm.ResourceId,
            $"更改规格 {vm.Name} → {newSize}",
            risk: RiskLevel.Medium,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.Medium),
            payload: new Dictionary<string, string>
            {
                ["newSize"] = newSize
            }), ct);

    public Task<OperationJob> DeleteAsync(
        VmSummary vm,
        IReadOnlyCollection<VmLinkedResourceKind> linkedResourcesToDelete,
        CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            ComputeModule.OperationDelete,
            vm.SubscriptionId,
            vm.ResourceId,
            $"删除虚拟机 {vm.Name}",
            // High 是"不可逆"的如实登记。但审批门**不靠这里** ——
            // DeleteVmHandler 恒返回 CannotBypass = true，那才是"即使用户关掉审批档也照样拦"
            // 的保证（引擎里唯一能压过用户设置的机制）。传 ShouldAutoApprove 只是与既有五个操作
            // 保持同一个写法，实际结果由 CannotBypass 决定。
            risk: RiskLevel.High,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.High),
            payload: new Dictionary<string, string>
            {
                // 只表达"用户想连带删哪些类别"，**不带任何资源 ID**：
                // 载荷会随待审批请求落盘，带 ID 等于允许改一个文件字符串就删任意资源。
                // 具体删哪些，由执行阶段读回真实挂载关系后求交集决定。
                [DeleteVmHandler.PayloadLinkedKinds] = string.Join(
                    ',', linkedResourcesToDelete.Select(kind => kind.ToString()))
            }), ct);

    private Task<OperationJob> SubmitAsync(
        string operationType, VmSummary vm, VmPowerState expected,
        string display, RiskLevel risk, CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            operationType,
            vm.SubscriptionId,
            vm.ResourceId,
            display,
            risk: risk,
            // 审批策略与影响分析各管一段：策略决定"这个风险等级要不要问用户"，
            // 影响分析决定"这个具体动作有没有值得说的后果"。两者取或，见 OperationEngine。
            preApproved: approvalPolicy.ShouldAutoApprove(risk),
            payload: new Dictionary<string, string>
            {
                ["expectedPowerState"] = expected.ToString()
            }), ct);
}
