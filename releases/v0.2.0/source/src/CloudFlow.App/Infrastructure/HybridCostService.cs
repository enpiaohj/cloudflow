using CloudFlow.Azure.Cost;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 成本查询的 Demo / 真实分流。
///
/// Demo（未登录）不编造成本金额；登录后一律走真实 Cost Management。
/// 成本是账单信息，示例金额即使标了“演示”也容易被误读为真实支出。
/// </summary>
public sealed class HybridCostService(
    CloudFlow.Azure.Cost.ArmCostService real,
    ScopeContext scopeContext) : ICostService
{
    public Task<(CostSummary? Summary, string? FailureReason)> GetMonthToDateAsync(
        string subscriptionId, CancellationToken ct = default) =>
        scopeContext.ActiveAccount is null
            ? Task.FromResult(((CostSummary?)null, (string?)"请登录 Azure 后查看真实成本数据。"))
            : real.GetMonthToDateAsync(subscriptionId, ct);
}
