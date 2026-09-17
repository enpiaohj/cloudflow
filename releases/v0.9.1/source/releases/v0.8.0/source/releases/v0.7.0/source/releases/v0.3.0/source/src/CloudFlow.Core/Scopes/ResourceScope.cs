namespace CloudFlow.Core.Scopes;

/// <summary>
/// ResourceScope —— CloudFlow 最重要的平台概念之一（设计文档 §8、§37）。
/// 所有 Module 的查询必须统一接受 ResourceScope（§38），
/// Home / Compute / Database / Network / Monitor / Cost / AI 全部使用同一个 Scope（§11）。
/// </summary>
public sealed class ResourceScope
{
    public string ScopeId { get; init; } = Guid.NewGuid().ToString("N");

    public string ScopeName { get; set; } = "";

    public ScopeMode Mode { get; set; } = ScopeMode.AllAccessible;

    public IReadOnlyList<string> AccountIds { get; set; } = [];

    public IReadOnlyList<string> TenantIds { get; set; } = [];

    public IReadOnlyList<string> SubscriptionIds { get; set; } = [];

    public IReadOnlyList<string> ManagementGroupIds { get; set; } = [];

    /// <summary>判断给定 Subscription 是否在当前 Scope 内。</summary>
    public bool ContainsSubscription(string subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return false;
        }

        return Mode switch
        {
            ScopeMode.SingleSubscription or ScopeMode.MultipleSubscriptions
                => SubscriptionIds.Contains(subscriptionId, StringComparer.OrdinalIgnoreCase),
            // Tenant / ManagementGroup 的成员解析后续迭代接入 ARM，当前按放行处理
            ScopeMode.Tenant or ScopeMode.ManagementGroup or ScopeMode.AllAccessible or ScopeMode.AllAccounts
                => true,
            _ => false
        };
    }

    /// <summary>用于 UI 展示的描述文本。</summary>
    public string Describe()
    {
        return Mode switch
        {
            ScopeMode.SingleSubscription => ScopeName,
            ScopeMode.MultipleSubscriptions => $"{ScopeName}（{SubscriptionIds.Count} 个订阅）",
            ScopeMode.Tenant => $"{ScopeName}（租户）",
            ScopeMode.ManagementGroup => $"{ScopeName}（管理组）",
            ScopeMode.AllAccessible => "全部可访问订阅",
            ScopeMode.AllAccounts => "全部账户",
            _ => ScopeName
        };
    }

    /// <summary>创建空白副本（避免共享可变引用）。</summary>
    public ResourceScope Clone() => new()
    {
        ScopeId = ScopeId,
        ScopeName = ScopeName,
        Mode = Mode,
        AccountIds = [.. AccountIds],
        TenantIds = [.. TenantIds],
        SubscriptionIds = [.. SubscriptionIds],
        ManagementGroupIds = [.. ManagementGroupIds]
    };
}
