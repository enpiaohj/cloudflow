using System.Text.Json;
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

    /// <summary>
    /// 默认配置<b>必须</b>是"未配置"状态，且默认值只能是占位符。
    /// </summary>
    /// <remarks>
    /// 这条在准备发布 v0.1.0 时被<b>有意反转</b>：原断言是 <c>Assert.True(config.IsConfigured)</c>，
    /// 即"内置一个可用的 Public Client ID"。而那个"可用"是靠把真实 App Registration ID
    /// <b>硬编码进源码</b>实现的 —— 真实值会随仓库一起提交、并永久留在 Git 历史里。
    /// 现在改为占位符，真实值只放被 gitignore 的 <c>appsettings.json</c>。
    /// <para>
    /// 反转后这条断言守的是<b>新要求</b>：默认配置里绝不能带任何真实 ClientId，
    /// 未显式配置时必须如实判为"未配置"，而不是拿假 ID 去登录再返回看不懂的 AADSTS 错误。
    /// </para>
    /// </remarks>
    [Fact]
    public void 默认配置_不带真实ClientId_必须显式配置才能用()
    {
        var config = new MsalAuthConfig();

        Assert.False(config.IsConfigured);
        Assert.StartsWith("SET-YOUR", config.ClientId, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", true)]
    [InlineData(" 8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b ", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("SET-YOUR-PUBLIC-CLIENT-ID", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidClientId_只接受非空GUID(string? value, bool expected)
    {
        Assert.Equal(expected, MsalAuthConfig.IsValidClientId(value));
    }

    [Theory]
    [InlineData("organizations", true)]
    [InlineData("Common", true)]
    [InlineData("8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", true)]
    [InlineData("contoso.onmicrosoft.com", true)]
    // ARM 只支持组织账户：个人账户租户不能用于这条登录链路
    [InlineData("consumers", false)]
    [InlineData("contoso", false)]
    [InlineData("https://login.microsoftonline.com/contoso.com", false)]
    [InlineData("", false)]
    public void IsValidTenant_接受组织租户标识_拒绝个人账户与非法格式(string value, bool expected)
    {
        Assert.Equal(expected, MsalAuthConfig.IsValidTenant(value));
    }

    [Fact]
    public void 保存配置_写入Azure节且保留文件中其它配置项()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, MsalAuthConfig.UserConfigFileName);
        try
        {
            File.WriteAllText(path,
                """{ "Logging": { "Level": "Debug" }, "Azure": { "Instance": "https://login.microsoftonline.com/" } }""");

            MsalAuthConfigFile.Save(path, " 8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b ", "organizations");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var azure = doc.RootElement.GetProperty("Azure");
            Assert.Equal("8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", azure.GetProperty("ClientId").GetString());
            Assert.Equal("organizations", azure.GetProperty("TenantId").GetString());
            Assert.Equal("https://login.microsoftonline.com/", azure.GetProperty("Instance").GetString());
            Assert.Equal("Debug", doc.RootElement.GetProperty("Logging").GetProperty("Level").GetString());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 保存配置_目录不存在时自动创建()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, "nested", "CloudFlow", MsalAuthConfig.UserConfigFileName);
        try
        {
            MsalAuthConfigFile.Save(path, "8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", "contoso.onmicrosoft.com");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("contoso.onmicrosoft.com", doc.RootElement.GetProperty("Azure").GetProperty("TenantId").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 保存配置_格式不合法时拒绝且不写文件()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, MsalAuthConfig.UserConfigFileName);
        try
        {
            Assert.Throws<ArgumentException>(() => MsalAuthConfigFile.Save(path, "not-a-guid", "organizations"));
            Assert.Throws<ArgumentException>(() =>
                MsalAuthConfigFile.Save(path, "8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", "consumers"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 保存配置_已有文件损坏时覆盖为有效配置()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, MsalAuthConfig.UserConfigFileName);
        try
        {
            File.WriteAllText(path, "{ not json");

            MsalAuthConfigFile.Save(path, "8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b", "organizations");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("8c8f3b2d-9d95-4d3c-8fcd-4fb5feac1f9b",
                doc.RootElement.GetProperty("Azure").GetProperty("ClientId").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cloudflow-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
