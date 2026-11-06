namespace CloudFlow.Core.Identity;

/// <summary>
/// 凭据上下文（P0 Spike 规范）：每次 Token 获取 / ARM Client 创建都必须
/// 完整绑定账户 + 租户 + 订阅 + Provider，杜绝跨账户串用。
/// 业务模块禁止只传 subscriptionId 或隐式使用"当前账户"。
/// </summary>
public sealed record CloudCredentialContext
{
    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required AuthenticationProviderType ProviderType { get; init; }

    public string? ProviderProfileId { get; init; }
}
