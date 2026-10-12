using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// 由当前激活账户构造 ARM 凭据上下文，企业（MSAL）与个人（嵌入式 Azure CLI）账户走同一入口。
///
/// 关键点：个人账户没有 MSAL 会话，取 Token 必须带上租户（az account get-access-token --tenant）；
/// 租户跟着**订阅**走 —— 见 <see cref="ResolveTenantId"/>。
/// </summary>
public static class ActiveAccountContext
{
    public static CloudCredentialContext Create(
        CloudAccount account,
        ScopeContext scopeContext,
        string? subscriptionId = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(scopeContext);

        var sub = subscriptionId
            ?? scopeContext.AvailableSubscriptions.FirstOrDefault()?.SubscriptionId
            ?? "";

        return new CloudCredentialContext
        {
            AccountId = account.AccountId,
            TenantId = ResolveTenantId(account, scopeContext, sub),
            SubscriptionId = sub,
            ProviderType = account.ProviderType,
            ProviderProfileId = account.ProviderProfileId
        };
    }

    /// <summary>
    /// 解析该订阅应使用的租户。顺序：该订阅登记的租户 → 账户主租户 → 任一已发现订阅的租户。
    ///
    /// 订阅优先于账户主租户，因为账户被邀请进客户租户（Guest）后，
    /// HomeTenantId 是它自己那个租户，拿它去取客户订阅的 Token 会 401。
    /// </summary>
    public static string ResolveTenantId(CloudAccount account, ScopeContext scopeContext, string? subscriptionId = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(scopeContext);

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            var fromSubscription = scopeContext.AvailableSubscriptions
                .FirstOrDefault(s => string.Equals(s.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
                ?.TenantId;
            if (!string.IsNullOrWhiteSpace(fromSubscription))
            {
                return fromSubscription;
            }
        }

        return account.HomeTenantId
            ?? scopeContext.AvailableSubscriptions
                .FirstOrDefault(s => !string.IsNullOrEmpty(s.TenantId))?.TenantId
            ?? "";
    }

    /// <summary>
    /// 从 Azure Resource ID 取出订阅 ID（<c>/subscriptions/{sub}/…</c>），大小写不敏感。
    /// 无法识别时返回 null —— 调用方据此退回"首个已发现订阅"，而不是抛错中断读取。
    /// </summary>
    public static string? SubscriptionIdOf(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }
}
