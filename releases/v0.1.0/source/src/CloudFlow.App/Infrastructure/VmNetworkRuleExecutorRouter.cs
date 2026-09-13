using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流 NSG 规则执行器：null = Demo（内存数据面），非 null = 真实 ARM。
/// 判据只看请求，不读 ScopeContext —— 账户切换不会把排队中的旧 Job 打到新订阅上。
/// </summary>
public sealed class VmNetworkRuleExecutorRouter(
    MockVmNetworkRuleExecutor mock,
    CloudFlow.Azure.Network.ArmVmNetworkRuleExecutor arm) : IVmNetworkRuleExecutor
{
    private IVmNetworkRuleExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<string?> OpenPortAsync(
        OperationRequest request, string nsgId, NsgRuleDraft draft, CancellationToken ct = default) =>
        For(request).OpenPortAsync(request, nsgId, draft, ct);

    public Task<string?> ChangePortAsync(
        OperationRequest request, string nsgId, string ruleName, int newPort, CancellationToken ct = default) =>
        For(request).ChangePortAsync(request, nsgId, ruleName, newPort, ct);

    public Task<string?> DeleteRuleAsync(
        OperationRequest request, string nsgId, string ruleName, CancellationToken ct = default) =>
        For(request).DeleteRuleAsync(request, nsgId, ruleName, ct);

    public Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default) =>
        For(request).CountVmsInSubnetAsync(request, subnetId, ct);
}
