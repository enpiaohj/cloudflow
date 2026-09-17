using CloudFlow.Core.Scopes;
using Xunit;

namespace CloudFlow.Core.Tests.Scopes;

public class ResourceScopeTests
{
    [Theory]
    [InlineData("sub-1", true)]
    [InlineData("sub-2", true)]
    [InlineData("sub-3", false)]
    public void ContainsSubscription_MultipleSubscriptions_按订阅ID列表判定(string subId, bool expected)
    {
        var scope = new ResourceScope
        {
            Mode = ScopeMode.MultipleSubscriptions,
            SubscriptionIds = ["sub-1", "sub-2"]
        };

        Assert.Equal(expected, scope.ContainsSubscription(subId));
    }

    [Fact]
    public void ContainsSubscription_SingleSubscription_只包含一个()
    {
        var scope = new ResourceScope
        {
            Mode = ScopeMode.SingleSubscription,
            SubscriptionIds = ["sub-1"]
        };

        Assert.True(scope.ContainsSubscription("sub-1"));
        Assert.False(scope.ContainsSubscription("sub-2"));
    }

    [Theory]
    [InlineData(ScopeMode.AllAccessible)]
    [InlineData(ScopeMode.AllAccounts)]
    [InlineData(ScopeMode.Tenant)]
    [InlineData(ScopeMode.ManagementGroup)]
    public void ContainsSubscription_开放模式_放行任意订阅(ScopeMode mode)
    {
        var scope = new ResourceScope { Mode = mode };

        Assert.True(scope.ContainsSubscription("any-sub"));
    }

    [Fact]
    public void ContainsSubscription_空订阅ID_返回False()
    {
        var scope = new ResourceScope { Mode = ScopeMode.MultipleSubscriptions, SubscriptionIds = ["sub-1"] };

        Assert.False(scope.ContainsSubscription(""));
        Assert.False(scope.ContainsSubscription(null!));
    }

    [Fact]
    public void Clone_副本与原对象独立_修改互不影响()
    {
        var original = new ResourceScope
        {
            Mode = ScopeMode.MultipleSubscriptions,
            SubscriptionIds = ["sub-1"]
        };

        var copy = original.Clone();
        Assert.Equal(original.SubscriptionIds, copy.SubscriptionIds);

        // 副本追加订阅不影响原对象
        copy.SubscriptionIds = [.. copy.SubscriptionIds, "sub-2"];
        Assert.Single(original.SubscriptionIds);
        Assert.Equal(2, copy.SubscriptionIds.Count);
    }

    [Fact]
    public void Describe_多订阅模式_包含订阅数量()
    {
        var scope = new ResourceScope
        {
            ScopeName = "Production",
            Mode = ScopeMode.MultipleSubscriptions,
            SubscriptionIds = ["a", "b", "c"]
        };

        Assert.Contains("Production", scope.Describe());
        Assert.Contains("3", scope.Describe());
    }
}

public class SavedScopeTests
{
    [Fact]
    public void SavedScope_创建_自动生成ScopeId与时间()
    {
        var scope = new SavedScope
        {
            Name = "Production",
            SubscriptionIds = ["sub-1", "sub-2"]
        };

        Assert.False(string.IsNullOrEmpty(scope.ScopeId));
        Assert.True(scope.CreatedAt <= DateTimeOffset.Now);
        Assert.Equal(2, scope.SubscriptionIds.Count);
    }
}
