using CloudFlow.Azure.Auth;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Auth;

public sealed class MsalAuthConfigTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("SET-YOUR-PUBLIC-CLIENT-ID", false)]
    [InlineData("8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", true)]
    public void IsConfigured_仅接受实际PublicClientId(string clientId, bool expected)
    {
        var config = new MsalAuthConfig { ClientId = clientId };

        Assert.Equal(expected, config.IsConfigured);
    }

    [Fact]
    public void 默认配置_使用系统浏览器回调与ARM权限()
    {
        var config = new MsalAuthConfig();

        Assert.Equal("http://localhost", config.RedirectUri);
        Assert.Equal("https://management.azure.com/.default", config.ManagementScope);
    }

    [Fact]
    public void 默认Authority_仅允许Azure管理所需的组织账户()
    {
        var config = new MsalAuthConfig();

        Assert.Equal("organizations", config.TenantId);
    }

    [Fact]
    public void 默认配置_提供内置PublicClientId()
    {
        var config = new MsalAuthConfig();

        Assert.True(config.IsConfigured);
    }
}
