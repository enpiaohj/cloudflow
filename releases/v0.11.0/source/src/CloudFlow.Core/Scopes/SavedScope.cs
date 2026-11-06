namespace CloudFlow.Core.Scopes;

/// <summary>
/// 用户保存的 Scope（设计文档 §10）。
/// 例如 "Production" = Prod-China + Prod-Korea + Prod-US，用户不用记 Subscription ID。
/// </summary>
public sealed class SavedScope
{
    public string ScopeId { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; set; }

    public string Description { get; set; } = "";

    public required IReadOnlyList<string> SubscriptionIds { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
