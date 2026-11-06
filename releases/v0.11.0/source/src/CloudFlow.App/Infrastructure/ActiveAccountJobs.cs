using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// Job 列表按当前账户过滤（设计文档：Job 必须保存认证上下文 AccountId + TenantId + ResourceId）。
///
/// 登录真实账户后仍展示其他账户（含 Demo 的 "demo-account"）的操作记录，
/// 会让用户把这些操作当成当前订阅里发生过的事 —— 这正是"任务页不是实时数据"的来源。
/// 未选择账户（Demo 模式）时不过滤，保留概念图的操作历史。
/// </summary>
public static class ActiveAccountJobs
{
    public static IEnumerable<OperationJob> For(
        ScopeContext scopeContext,
        IEnumerable<OperationJob> jobs) =>
        scopeContext.ActiveAccount is { } account
            ? jobs.Where(job =>
                string.Equals(job.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase))
            : jobs;
}
