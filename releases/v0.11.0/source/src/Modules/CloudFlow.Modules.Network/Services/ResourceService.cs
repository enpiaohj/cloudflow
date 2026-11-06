using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <inheritdoc cref="IResourceService" />
public sealed class ResourceService(
    IOperationEngine engine,
    OperationRequestFactory requests,
    IApprovalPolicy approvalPolicy) : IResourceService
{
    public Task<OperationJob> DeleteAsync(
        string subscriptionId, string resourceId, string resourceName, string resourceType,
        CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            GenericResourceModule.OperationDelete,
            subscriptionId,
            resourceId,
            $"删除资源 {resourceName}",
            // 与资源组删除同一条纪律：风险如实登记为 High，审批门不靠这里——
            // DeleteResourceHandler 恒返回 CannotBypass = true。
            risk: RiskLevel.High,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.High),
            payload: new Dictionary<string, string>
            {
                [GenericResourceModule.PayloadResourceType] = resourceType
            }), ct);
}
