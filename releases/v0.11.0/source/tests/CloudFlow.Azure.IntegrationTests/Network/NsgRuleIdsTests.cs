using CloudFlow.Modules.Network.Models;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

/// <summary>
/// 规则 Resource ID 的拆装。
///
/// 这组断言守的是一个真实 Bug：读路径按 {nsgId}/securityRules/{name} 构造 RuleId，
/// 写路径却在 Open Port 里凭空编造 "{vmId}/rules/{name}-{guid}"，
/// 于是真实账户下永远定位不到目标 NSG，改动只落在内存。
/// </summary>
public class NsgRuleIdsTests
{
    private const string NsgId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-web/providers/Microsoft.Network/networkSecurityGroups/nsg-web-prod";

    [Fact]
    public void 拆解_取回所属NSG与规则名()
    {
        var split = NsgRuleIds.Split($"{NsgId}/securityRules/HTTPS");

        Assert.NotNull(split);
        Assert.Equal(NsgId, split!.Value.NsgId);
        Assert.Equal("HTTPS", split.Value.RuleName);
    }

    [Fact]
    public void 组装与拆解_可往返()
    {
        Assert.Equal($"{NsgId}/securityRules/HTTPS", NsgRuleIds.Compose(NsgId, "HTTPS"));
        Assert.Equal("HTTPS", NsgRuleIds.Split(NsgRuleIds.Compose(NsgId, "HTTPS"))!.Value.RuleName);
    }

    [Fact]
    public void 组装_容忍NSGID末尾斜杠()
    {
        Assert.Equal($"{NsgId}/securityRules/HTTPS", NsgRuleIds.Compose($"{NsgId}/", "HTTPS"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HTTPS")]
    // 过去 Open Port 编造的假 ID：不含 securityRules 段，必须被拒绝而不是猜
    [InlineData("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/WEB01/rules/port-8080-ab12cd34")]
    public void 形状不符_返回null(string? ruleId)
    {
        Assert.Null(NsgRuleIds.Split(ruleId));
    }

    [Fact]
    public void 拆解_大小写不敏感()
    {
        var split = NsgRuleIds.Split($"{NsgId}/SecurityRules/HTTPS");

        Assert.NotNull(split);
        Assert.Equal(NsgId, split!.Value.NsgId);
        Assert.Equal("HTTPS", split.Value.RuleName);
    }
}
