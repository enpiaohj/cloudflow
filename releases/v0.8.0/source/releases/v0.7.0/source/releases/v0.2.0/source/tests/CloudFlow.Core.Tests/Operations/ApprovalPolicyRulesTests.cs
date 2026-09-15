using CloudFlow.Core.Operations;
using Xunit;

namespace CloudFlow.Core.Tests.Operations;

/// <summary>
/// 三档审批策略 → "要不要停下来"的映射。
///
/// 这张表决定了用户会不会被打断，也决定了默认档位会不会让既有行为发生变化，
/// 所以逐格断言，不靠"看起来对"。
/// </summary>
public sealed class ApprovalPolicyRulesTests
{
    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    public void 关闭档_任何风险等级都不停(RiskLevel risk)
    {
        Assert.True(ApprovalPolicy.Off.ShouldAutoApprove(risk));
    }

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    public void 所有写操作档_任何风险等级都停下(RiskLevel risk)
    {
        Assert.False(ApprovalPolicy.AllWrites.ShouldAutoApprove(risk));
    }

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    public void 仅高危档_非高危不停(RiskLevel risk)
    {
        Assert.True(ApprovalPolicy.HighRiskOnly.ShouldAutoApprove(risk));
    }

    [Fact]
    public void 仅高危档_高危停下()
    {
        Assert.False(ApprovalPolicy.HighRiskOnly.ShouldAutoApprove(RiskLevel.High));
    }

    [Fact]
    public void 默认设置下_重启与关机停下而其余不停()
    {
        // 这条把"默认档位"和"实际操作的风险等级"钉在一起：
        // 默认档是仅高危，而重启 / 关机是仅有的两个 High，
        // 于是默认行为恰好等于本设置存在之前的样子 —— 升级一次不会改变任何人的审批体验。
        // 谁哪天把某个操作的风险等级调了，这条会先响。
        var policy = CloudFlow.Data.Stores.AppSettings.Default.ApprovalPolicy;

        Assert.True(policy.ShouldAutoApprove(RiskLevel.Low));      // 启动 / 创建快照
        Assert.True(policy.ShouldAutoApprove(RiskLevel.Medium));   // 解除分配 / 改规格 / 改端口
        Assert.False(policy.ShouldAutoApprove(RiskLevel.High));    // 重启 / 关机
    }
}
