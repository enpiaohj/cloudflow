namespace CloudFlow.Core.Identity;

/// <summary>
/// 统一账户模型（设计文档 §34；P0 Spike 规范 §十三）。
/// 身份唯一性 = ProviderType + Provider 原生 ID；UPN / 显示名称不作为主键。
/// 仅保存账户元数据，禁止保存 Password / Token / Secret。
/// </summary>
public sealed class CloudAccount
{
    /// <summary>Provider 原生账户标识（MSAL HomeAccountId.Identifier / CLI Profile 等），CloudFlow 内部账户唯一键。</summary>
    public required string AccountId { get; init; }

    /// <summary>UPN，如 hongji@contoso.com。</summary>
    public required string Username { get; init; }

    public string DisplayName { get; init; } = "";

    /// <summary>该账户的身份 Provider 类型（决定 Token 获取链路）。</summary>
    public required AuthenticationProviderType ProviderType { get; init; }

    /// <summary>Provider 私有状态关联（如 Embedded Azure CLI Profile 的 GUID）。</summary>
    public string? ProviderProfileId { get; init; }

    public string? HomeTenantId { get; init; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
