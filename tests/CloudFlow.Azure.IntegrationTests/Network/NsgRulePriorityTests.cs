using CloudFlow.Modules.Network.Models;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

public sealed class NsgRulePriorityTests
{
    [Fact]
    public void 连续创建时避开已用优先级() =>
        Assert.Equal(420, NsgRulePriorities.Suggest([400, 410]));

    [Fact]
    public void 推荐按十递增但不要求所有已有优先级都是十的倍数() =>
        Assert.Equal(410, NsgRulePriorities.Suggest([400, 401]));

    [Fact]
    public void 十的倍数全部占用时仍可使用合法空闲整数() =>
        Assert.Equal(401, NsgRulePriorities.Suggest(Enumerable.Range(10, 400).Select(x => x * 10)));

    [Fact]
    public void 高位耗尽时使用低位空闲值() =>
        Assert.Equal(100, NsgRulePriorities.Suggest(Enumerable.Range(400, 3697)));

    [Fact]
    public void 全部耗尽时不建议非法值() =>
        Assert.Null(NsgRulePriorities.Suggest(Enumerable.Range(100, 3997)));
}
