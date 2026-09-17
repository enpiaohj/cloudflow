namespace CloudFlow.Azure.Cost;

/// <summary>
/// 本月成本摘要。
/// 金额与币种分离保存：把 "$482.21" 这样的字符串一路传下来，就没法做区间比较或排序，
/// 而币种是订阅属性，必须如实显示 —— 真实账户可能是 CNY。
/// </summary>
public sealed record CostSummary
{
    /// <summary>本月至今的实际成本。</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 币种代码，如 USD / CNY。</summary>
    public required string Currency { get; init; }

    /// <summary>统计区间说明，如 "2026-09-01 起至今"。</summary>
    public required string PeriodText { get; init; }

    /// <summary>数据获取时间。</summary>
    public required DateTimeOffset RetrievedAt { get; init; }
}

/// <summary>
/// 成本查询（设计文档 Home 成本洞察）。
/// 目前只提供「本月至今的实际成本」—— 不做预测、不做趋势，因为 Cost Management 的
/// 预测与分组查询限流更严，取不到时反而会显示成"成本为零"。
/// </summary>
public interface ICostService
{
    /// <summary>读取本月至今成本；无法取得时返回 null 并给出原因，绝不返回 0 冒充"没有花费"。</summary>
    Task<(CostSummary? Summary, string? FailureReason)> GetMonthToDateAsync(
        string subscriptionId, CancellationToken ct = default);
}
