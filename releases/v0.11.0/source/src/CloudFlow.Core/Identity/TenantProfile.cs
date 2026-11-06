namespace CloudFlow.Core.Identity;

/// <summary>
/// Tenant 档案（设计文档 §35）。
/// 一个 Account 可访问多个 Tenant，Account ≠ Tenant，数据模型必须分离（§7）。
/// </summary>
public sealed class TenantProfile
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public string DisplayName { get; init; } = "";

    public string DefaultDomain { get; init; } = "";

    public DateTimeOffset? LastRefreshedAt { get; set; }
}
