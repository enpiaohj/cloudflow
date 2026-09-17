using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <inheritdoc cref="IResourceGroupService" />
public sealed class ResourceGroupService(
    IOperationEngine engine,
    OperationRequestFactory requests,
    IApprovalPolicy approvalPolicy) : IResourceGroupService
{
    public Task<OperationJob> DeleteAsync(
        string subscriptionId, string resourceGroupName, CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            ResourceGroupModule.OperationDelete,
            subscriptionId,
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}",
            $"删除资源组 {resourceGroupName}",
            // 级联删除，破坏半径比删单台虚拟机更大：风险如实登记为 High。
            // 审批门不靠这里——DeleteResourceGroupHandler 恒返回 CannotBypass = true，
            // 与 DeleteVmHandler 同一条纪律（见该 Handler 的注释）。
            risk: RiskLevel.High,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.High)), ct);
}
