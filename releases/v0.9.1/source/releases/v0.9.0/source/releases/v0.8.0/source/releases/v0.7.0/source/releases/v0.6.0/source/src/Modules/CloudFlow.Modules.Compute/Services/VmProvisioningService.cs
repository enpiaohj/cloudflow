using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary><see cref="IVmProvisioningService"/> 的实现。</summary>
/// <remarks>
/// ResourceId 在<b>提交时</b>还不存在（资源尚未创建），因此按 Azure 命名规则预拼一个 ——
/// 它是这次操作的目标标识，Job、审计与 Verify 都挂在它上面；
/// 创建成功后它恰好就是真实资源的 ID。
/// </remarks>
public sealed class VmProvisioningService(
    IOperationEngine engine,
    OperationRequestFactory requests,
    IApprovalPolicy approvalPolicy) : IVmProvisioningService
{
    public Task<OperationJob> CreateAsync(
        string subscriptionId, string resourceGroupName, string region,
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var vmName = parameters[CreateVmHandler.PayloadVmName];
        var payload = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            // 区域是提交契约的独立参数；在这里统一写入持久化载荷，供执行器创建 ARM 资源时使用。
            [CreateVmHandler.PayloadRegion] = region
        };
        var resourceId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Compute/virtualMachines/{vmName}";

        return engine.SubmitAsync(requests.Create(
            ComputeModule.OperationCreate,
            subscriptionId,
            resourceId,
            $"创建虚拟机 {vmName}",
            // 新建可逆（可删），登记 Medium；审批策略在默认「仅高危」档会放行 ——
            // 与删除（不可逆、CannotBypass 恒拦）刻意区别对待，见 §87.2/§87.3
            risk: RiskLevel.Medium,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.Medium),
            payload: payload), ct);
    }
}
