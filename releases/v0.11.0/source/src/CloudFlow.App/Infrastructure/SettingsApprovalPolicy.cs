using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 审批策略的实现：把设置页里的档位接到 <see cref="ApprovalPolicyRules"/> 上。
///
/// 每次判定都现读 <see cref="AppSettingsStore.Current"/>，所以在设置页改完立即生效，
/// 不需要重启，也不需要把策略缓存到某个 ViewModel 里。
/// 映射规则本身住在 Core（可脱离 WPF 直接测），这里只负责取设置。
/// </summary>
public sealed class SettingsApprovalPolicy(AppSettingsStore settings) : IApprovalPolicy
{
    public bool ShouldAutoApprove(RiskLevel risk) =>
        settings.Current.ApprovalPolicy.ShouldAutoApprove(risk);
}
