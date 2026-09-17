using System.IO;
using CloudFlow.Terminal.Security;
using CloudFlow.Terminal.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Terminal.Tests.Ssh;

/// <summary>
/// 「连接根本没能发起」的状态处理。真实踩到的现象：终端渲染层（WebView2）初始化失败时，
/// 会话永远停在 Idle，标签条一直显示「待连接」，用户得不到任何失败信号，也无从知道
/// 重新点「连接」就能换一个全新会话重试。
/// </summary>
public sealed class SshSessionStartFailureTests
{
    private static SshSession Build()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"),
            "known-hosts.json");
        var policy = new SshHostKeyPolicy(new KnownHostStore(path), _ => Task.FromResult(false));
        return new SshSession(new SshConnectionOptions
        {
            Host = "20.0.0.1",
            Username = "azureuser",
            AuthType = SshAuthType.Password,
            Password = "not-a-real-secret",
            HostKeyPolicy = policy
        }, NullLogger.Instance);
    }

    [Fact]
    public async Task 渲染层起不来时_会话应从待连接转入连接失败()
    {
        var session = Build();
        var states = new List<SshSessionState>();
        session.StateChanged += (_, state) => states.Add(state);

        session.MarkStartFailed("终端组件初始化失败。");

        Assert.Equal(SshSessionState.Failed, session.State);
        Assert.Contains("终端组件初始化失败", session.ErrorMessage);
        Assert.Contains(SshSessionState.Failed, states);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task 已进入连接过程后_MarkStartFailed不得改写状态()
    {
        var session = Build();
        session.MarkStartFailed("首次失败");

        // 第二次调用不得覆盖既有的失败信息；已 Failed 也不再重复触发状态事件
        session.MarkStartFailed("另一次失败");

        Assert.Equal(SshSessionState.Failed, session.State);
        Assert.Contains("首次失败", session.ErrorMessage);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task 释放后再调用_MarkStartFailed不应抛出()
    {
        var session = Build();
        await session.DisposeAsync();

        session.MarkStartFailed(" disposed 之后再调用");

        Assert.Equal(SshSessionState.Idle, session.State);
    }
}
