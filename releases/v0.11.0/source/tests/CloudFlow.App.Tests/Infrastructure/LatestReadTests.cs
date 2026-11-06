using CloudFlow.App.Infrastructure;
using Xunit;

namespace CloudFlow.App.Tests.Infrastructure;

public sealed class LatestReadTests
{
    [Fact]
    public void 新读取取消旧读取且旧结果不可应用()
    {
        var reads = new LatestRead();
        using var first = reads.Begin();
        using var second = reads.Begin();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public void 用户取消后即使服务返回也不可应用结果()
    {
        var reads = new LatestRead();
        using var read = reads.Begin();
        reads.Cancel();
        Assert.True(read.Token.IsCancellationRequested);
        Assert.False(read.IsCurrent);
        using var retry = reads.Begin();
        Assert.True(retry.IsCurrent);
        Assert.False(retry.Token.IsCancellationRequested);
    }

    [Fact]
    public void 旧读取结束不能取消新读取()
    {
        var reads = new LatestRead();
        var first = reads.Begin();
        using var second = reads.Begin();
        first.Dispose();
        first.Dispose();
        Assert.True(second.IsCurrent);
        Assert.False(second.Token.IsCancellationRequested);
    }
}
