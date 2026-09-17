using CloudFlow.Azure.Cost;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 成本查询的 Demo / 真实分流。
///
/// Demo（未登录）返回概念图 1 的示例金额；登录后一律走真实 Cost Management ——
/// 演示金额绝不出现在真实订阅的首页，那会被当成真实账单。
/// </summary>
public sealed class HybridCostService(
    CloudFlow.Azure.Cost.ArmCostService real,
    ScopeContext scopeContext) : ICostService
{
    /// <summary>与概念图 1 一致的演示读数。</summary>
    private static readonly CostSummary Demo = new()
    {
        Amount = 482.21m,
        Currency = "USD",
        PeriodText = "本月至今（演示数据）",
        RetrievedAt = DateTimeOffset.Now
    };

    public Task<(CostSummary? Summary, string? FailureReason)> GetMonthToDateAsync(
        string subscriptionId, CancellationToken ct = default) =>
        scopeContext.ActiveAccount is null
            ? Task.FromResult(((CostSummary?)Demo, (string?)null))
            : real.GetMonthToDateAsync(subscriptionId, ct);
}
