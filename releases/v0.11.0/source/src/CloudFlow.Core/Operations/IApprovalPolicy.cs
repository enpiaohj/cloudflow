namespace CloudFlow.Core.Operations;

/// <summary>
/// 风险类审批的判定：某个风险等级的写操作在提交时是否视为"用户已预先确认"。
///
/// **只管风险类审批。** 设计文档 §25 的共享子网 NSG 影响面确认不经过这里 ——
/// 它由 ImpactAssessment.CannotBypass 在 Operation Engine 里兜底，任何策略档位都绕不过。
/// 把两者混起来理解，就会以为把策略调成「关闭」能跳过 §25，那正是要避免的。
///
/// 调用点在提交侧（UI / 服务），引擎本身不读策略：引擎只认请求里带没带 PreApproved，
/// 加上 CannotBypass 那一层硬约束。这样策略换实现、换存储都不影响引擎的行为契约。
/// </summary>
public interface IApprovalPolicy
{
    /// <summary>true = 直接执行；false = 停下等用户审批。</summary>
    bool ShouldAutoApprove(RiskLevel risk);
}
