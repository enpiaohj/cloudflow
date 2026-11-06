namespace CloudFlow.Core.Identity;

/// <summary>
/// Subscription 档案（设计文档 §36）。
/// </summary>
public sealed class SubscriptionProfile
{
    public required string SubscriptionId { get; init; }

    public required string TenantId { get; init; }

    public string DisplayName { get; init; } = "";

    /// <summary>Azure Subscription State：Enabled / Disabled / Warned 等。</summary>
    public string State { get; init; } = "Enabled";

    public bool IsSelected { get; set; }

    public DateTimeOffset? LastRefreshedAt { get; set; }
}
