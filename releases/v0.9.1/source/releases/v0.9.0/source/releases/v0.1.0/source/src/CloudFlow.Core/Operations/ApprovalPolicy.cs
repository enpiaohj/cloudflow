namespace CloudFlow.Core.Operations;

/// <summary>
/// 审批策略档位：决定"风险类"写操作在提交时是否停下来等用户确认。
///
/// 这里管的是**风险类**审批。设计文档 §25 的共享子网 NSG 影响面确认不归它管 ——
/// 那条审批被 <c>ImpactAssessment.CannotBypass</c> 标记为任何档位都不可绕过，
/// 见 <c>OperationEngine.SubmitAsync</c>。把档位理解成"所有审批的总开关"就会误以为
/// 关闭档能跳过 §25，这正是要避免的误解。
/// </summary>
public enum ApprovalPolicy
{
    /// <summary>关闭：风险类操作直接执行，不停。</summary>
    Off,

    /// <summary>
    /// 仅高危（默认）：只有 <see cref="RiskLevel.High"/> 的操作停下来审批。
    /// 这是本设置存在之前的行为，所以新装 / 设置文件损坏时都回退到它。
    /// </summary>
    HighRiskOnly,

    /// <summary>所有写操作：不区分风险等级，任何写操作都停下来审批。</summary>
    AllWrites
}

/// <summary>
/// 档位 → "要不要停下来"的映射。放在 Core 而不是设置页里，
/// 是为了让它能脱离 WPF 被直接测：这张表决定了用户会不会被打断，写错了没人看得出来。
/// </summary>
public static class ApprovalPolicyRules
{
    /// <summary>
    /// true = 直接执行；false = 停下等用户审批。
    ///
    /// 这一层只管**风险类**审批。它返回 true 并不代表操作一定不停 ——
    /// 影响分析若判定 <c>CannotBypass</c>（§25 共享子网 NSG），引擎照样会拦。
    /// </summary>
    public static bool ShouldAutoApprove(this ApprovalPolicy policy, RiskLevel risk) =>
        policy switch
        {
            ApprovalPolicy.Off => true,
            ApprovalPolicy.AllWrites => false,
            _ => risk != RiskLevel.High
        };
}
