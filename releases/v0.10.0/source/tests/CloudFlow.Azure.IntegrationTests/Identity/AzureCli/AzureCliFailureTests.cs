using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// AzureCliFailure 的归因测试：把真实的瞬断信号识别成"可重试的网络问题"，给出可行动的措辞，
/// 并且**不**把权限 / 参数 / 账户问题误判成可重试。
/// </summary>
public sealed class AzureCliFailureTests
{
    [Theory]
    [InlineData("ConnectionResetError: [WinError 10054] 远程主机强迫关闭了一个现有的连接。")]
    [InlineData("urllib3.exceptions.ProtocolError: (Connection aborted, ConnectionResetError(10054, 远程主机强迫关闭, None, 10054, None))")]
    [InlineData("connectionpool.py: __init__.py1 ... retries (ConnectionError: 10060 connect timed out)")]
    [InlineData("GetAddrInfo failed: Name or service not known")]
    [InlineData("proxyerror: connecting to proxy timed out")]
    public void 瞬断stderr_判为可重试网络问题(string stderr)
    {
        Assert.True(AzureCliFailure.Classify(1, stderr).IsTransientNetwork);
    }

    [Theory]
    [InlineData("ERROR: (AuthorizationFailed) client does not have authorization to perform action")]
    [InlineData("ERROR: (InvalidAuthenticationInfo) bad token")]
    [InlineData("ERROR: The request is missing required parameter 'tenant'")]
    [InlineData("ERROR: AADSTS7000218: invalid client credentials")]
    [InlineData("ERROR: Invalid subscriptionId.")]
    public void 非网络失败_不判为可重试网络问题(string stderr)
    {
        Assert.False(AzureCliFailure.Classify(1, stderr).IsTransientNetwork);
    }

    [Fact]
    public void 空stderr_不判为可重试()
    {
        Assert.False(AzureCliFailure.Classify(1, "").IsTransientNetwork);
    }

    [Fact]
    public void 网络失败的措辞_收敛为可行动的一句_不掺traceback()
    {
        var message = AzureCliFailure.Describe(
            "获取访问令牌", 1, "Traceback... ConnectionResetError: [WinError 10054] 远程主机强迫关闭");

        Assert.Contains("网络连接中断", message);
        Assert.DoesNotContain("Traceback", message);
        Assert.DoesNotContain("10054", message);
    }

    [Fact]
    public void 非网络失败的措辞_保留退出码与脱敏详情()
    {
        var message = AzureCliFailure.Describe("获取订阅列表", 1, "ERROR: (AuthorizationFailed) denied");

        Assert.Contains("获取订阅列表失败", message);
        Assert.Contains("AuthorizationFailed", message);
        Assert.Contains("退出码 1", message);
    }
}