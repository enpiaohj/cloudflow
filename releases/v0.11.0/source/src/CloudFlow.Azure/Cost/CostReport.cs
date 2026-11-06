using System.Globalization;
namespace CloudFlow.Azure.Cost;

public sealed record ResourceCost(string SubscriptionId, string ResourceId, string ResourceGroup, decimal Amount, string Currency)
{
    public string ResourceName => string.IsNullOrWhiteSpace(ResourceId) ? "未关联资源（其他费用）" : ResourceId.TrimEnd('/').Split('/').Last();
    public string ResourceType
    {
        get
        {
            var parts = ResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var provider = Array.FindIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
            if (provider < 0 || provider + 2 >= parts.Length) return ResourceId.Length == 0 ? "其他费用" : "资源";
            return parts[provider + 1] + "/" + string.Join("/", parts.Skip(provider + 2).Where((_, index) => index % 2 == 0));
        }
    }
    public string AmountText => $"{Amount.ToString("N2", CultureInfo.CurrentCulture)} {Currency}";
}
public sealed record ResourceGroupCost(string SubscriptionId, string ResourceGroup, decimal Amount, string Currency, int ResourceCount);

public sealed record CostReport(IReadOnlyList<ResourceCost> Rows, IReadOnlyList<string> Failures, DateTimeOffset RetrievedAt)
{
    public IReadOnlyList<ResourceGroupCost> GroupByResourceGroup() => Rows
        .GroupBy(r => (r.SubscriptionId.ToLowerInvariant(), r.ResourceGroup.ToLowerInvariant(), r.Currency.ToUpperInvariant()))
        .Select(g => new ResourceGroupCost(g.First().SubscriptionId, g.First().ResourceGroup, g.Sum(r => r.Amount), g.First().Currency,
            g.Where(r => r.ResourceId.Length > 0).Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
        .OrderBy(r => r.Currency).ThenByDescending(r => r.Amount).ToArray();
    public string TotalText => Rows.Count == 0 ? "暂无费用记录" : string.Join(" · ", Rows.GroupBy(r=>r.Currency, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g=>g.Key).Select(g=>$"{g.Sum(r=>r.Amount).ToString("N2", CultureInfo.CurrentCulture)} {g.Key}"));
}
