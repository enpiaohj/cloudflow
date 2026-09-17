using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;

namespace CloudFlow.Core.Operations;

/// <summary>
/// 构造 <see cref="OperationRequest"/> 并注入认证上下文（设计文档 §31）。
///
/// 这是**唯一在提交时读取 <see cref="ScopeContext"/> 的地方**：身份在提交那一刻被"盖"进请求，
/// 之后 Handler / Executor / Job 一律只从请求取身份，不再读环境态 ——
/// 否则切换账户后，排队中的旧 Job 会拿新账户的凭据去打旧订阅。
///
/// 未登录（Demo）时回退 <see cref="DemoIdentity"/> 且 <c>ProviderType</c> 留 null，
/// 执行器据此走 Mock 路径；真实 ARM 路径要求 ProviderType 非空。
/// </summary>
public sealed class OperationRequestFactory(ScopeContext scopeContext)
{
    public OperationRequest Create(
        string operationType,
        string subscriptionId,
        string resourceId,
        string display,
        RiskLevel risk = RiskLevel.Low,
        bool preApproved = false,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var account = scopeContext.ActiveAccount;

        return new OperationRequest
        {
            OperationType = operationType,
            AccountId = account?.AccountId ?? DemoIdentity.AccountId,
            TenantId = ResolveTenantId(account, subscriptionId),
            AccountDisplayName = DisplayNameOf(account),
            SubscriptionId = subscriptionId,
            ProviderType = account?.ProviderType,
            ProviderProfileId = account?.ProviderProfileId,
            ResourceId = resourceId,
            Risk = risk,
            PreApproved = preApproved,
            Display = display,
            Payload = payload ?? new Dictionary<string, string>()
        };
    }

    /// <summary>
    /// 租户必须跟着**订阅**走，不能跟着账户主租户走：账户被邀请进客户租户后，
    /// HomeTenantId 是它自己那个租户，拿它为该订阅取 Token 会 401。
    /// 顺序：该订阅登记的租户 → 账户主租户 → 空串（交给 Validate 报错）。
    ///
    /// 已登录却解析不出租户时**不能**回退演示租户：那会让真实请求带着假身份出发。
    /// </summary>
    private string ResolveTenantId(CloudAccount? account, string subscriptionId)
    {
        var fromSubscription = scopeContext.AvailableSubscriptions
            .FirstOrDefault(s => string.Equals(s.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
            ?.TenantId;
        if (!string.IsNullOrWhiteSpace(fromSubscription))
        {
            return fromSubscription;
        }

        if (account is null)
        {
            return DemoIdentity.TenantId;
        }

        return account.HomeTenantId ?? "";
    }

    /// <summary>
    /// 任务列表「账户」列的可读来源。DisplayName 可能为空（CLI 身份常常只给 UPN），
    /// 此时回退 Username —— 空串会让历史任务永远显示为 "—"，审计上说不清是谁执行的。
    /// Demo 模式没有账户，用固定文案标明这是演示数据。
    /// </summary>
    private static string DisplayNameOf(CloudAccount? account)
    {
        if (account is null)
        {
            return "演示账户";
        }

        return !string.IsNullOrWhiteSpace(account.DisplayName) ? account.DisplayName : account.Username;
    }
}
