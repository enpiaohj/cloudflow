namespace CloudFlow.Core.Identity;

/// <summary>
/// Microsoft 登录账户（设计文档 §34）。
/// 仅保存账户元数据，禁止保存 Password / Token。
/// </summary>
public sealed class CloudAccount
{
    /// <summary>MSAL Account Identifier，CloudFlow 内部账户唯一键。</summary>
    public required string AccountId { get; init; }

    /// <summary>UPN，如 hongji@contoso.com。</summary>
    public required string Username { get; init; }

    public string DisplayName { get; init; } = "";

    public string? HomeTenantId { get; init; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
