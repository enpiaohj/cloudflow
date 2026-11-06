namespace CloudFlow.Core.Identity;

/// <summary>
/// 订阅发现（P1，设计文档 §85 第 5 步）。
/// 登录后枚举账户可访问的全部 Subscription（含多 Tenant 识别，§72）。
/// </summary>
public interface ISubscriptionDiscoveryService
{
    Task<IReadOnlyList<SubscriptionProfile>> DiscoverAsync(AccountSession session, CancellationToken ct = default);
}
