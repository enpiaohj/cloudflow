namespace CloudFlow.Core.Scopes;

/// <summary>
/// Scope 模式（设计文档 §37）。
/// </summary>
public enum ScopeMode
{
    /// <summary>单个 Subscription。</summary>
    SingleSubscription,

    /// <summary>多个 Subscription。</summary>
    MultipleSubscriptions,

    /// <summary>整个 Tenant。</summary>
    Tenant,

    /// <summary>Management Group。</summary>
    ManagementGroup,

    /// <summary>当前账户全部可访问 Subscription。</summary>
    AllAccessible,

    /// <summary>跨全部已登录 Account（P2 All Accounts View）。</summary>
    AllAccounts
}
