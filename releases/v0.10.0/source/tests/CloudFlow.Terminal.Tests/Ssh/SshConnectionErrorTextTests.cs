using System.Net.Sockets;
using CloudFlow.Terminal.Ssh;
using Renci.SshNet.Common;
using Xunit;

namespace CloudFlow.Terminal.Tests.Ssh;

/// <summary>底层异常 → 标准化错误码的映射表（RemoteFlow MapError 同款）。</summary>
public sealed class SshConnectionErrorTextTests
{
    [Fact]
    public void 认证异常_映射为认证失败()
    {
        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed,
            SshConnectionErrorText.Map(new SshAuthenticationException("bad"), SshConnectionErrorCode.None));
    }

    [Fact]
    public void 连接超时_映射为超时()
    {
        var ex = new SocketException((int)SocketError.TimedOut);
        Assert.Equal(SshConnectionErrorCode.Timeout,
            SshConnectionErrorText.Map(ex, SshConnectionErrorCode.None));
    }

    [Fact]
    public void 主机不可解析_映射为主机未找到()
    {
        var ex = new SocketException((int)SocketError.HostNotFound);
        Assert.Equal(SshConnectionErrorCode.HostNotFound,
            SshConnectionErrorText.Map(ex, SshConnectionErrorCode.None));
    }

    [Fact]
    public void HostKey失败_优先于底层异常()
    {
        // 握手被 HostKey 策略中止时，无论底层异常是什么，都报 HostKey 原因
        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch,
            SshConnectionErrorText.Map(
                new SocketException((int)SocketError.TimedOut), SshConnectionErrorCode.HostKeyMismatch));
    }

    [Fact]
    public void 每个错误码都有中文文案()
    {
        foreach (var code in Enum.GetValues<SshConnectionErrorCode>())
        {
            if (code == SshConnectionErrorCode.None)
            {
                continue;
            }

            var text = SshConnectionErrorText.Describe(code);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(code.ToString(), text);
        }
    }
}
