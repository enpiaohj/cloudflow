using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 登录服务配置入口的静态护栏。单文件发布包里没有 appsettings.json，也不随包分发任何真实 ClientId，
/// 正式版只能在「设置 → 账户 → 登录服务」里填写；这里核对这条链路的源码约定
/// （App 测试项目不引用 WPF，做法同 <see cref="ResourceGroupsPageStaticTests"/>）。
/// </summary>
public sealed class LoginServiceConfigStaticTests
{
    [Fact]
    public void 用户目录配置必须在程序目录配置之后加载_后者覆盖前者()
    {
        var app = File.ReadAllText(Path.Combine(AppDirectory(), "App.xaml.cs"));

        var exeConfig = app.IndexOf(".AddJsonFile(\"appsettings.json\"", StringComparison.Ordinal);
        var userConfig = app.IndexOf(".AddJsonFile(userConfigFile", StringComparison.Ordinal);
        Assert.True(exeConfig >= 0, "程序目录 appsettings.json 仍须作为开发调试的配置来源。");
        Assert.True(userConfig > exeConfig, "用户目录配置必须在程序目录配置之后添加，才能覆盖它。");
        Assert.Contains("MsalAuthConfig.UserConfigFileName", app, StringComparison.Ordinal);
    }

    [Fact]
    public void 设置页必须提供登录服务配置入口()
    {
        var page = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"));
        Assert.Contains("Text=\"{Binding ClientIdInput, UpdateSourceTrigger=PropertyChanged}\"", page, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TenantIdInput, UpdateSourceTrigger=PropertyChanged}\"", page, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveAuthConfigCommand}\"", page, StringComparison.Ordinal);
        // 账户范围是选择而不是手填 organizations；租户 ID 只在选"单个组织"时出现。
        Assert.Contains("ItemsSource=\"{Binding TenantModeOptions}\"", page, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding TenantMode, Mode=TwoWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsSingleTenant", page, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "SettingsViewModel.cs"));
        Assert.Contains("MsalAuthConfigFile.Save(UserAuthConfigPath", viewModel, StringComparison.Ordinal);
        Assert.Contains("MsalAuthConfig.IsValidClientId(clientId)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void 未配置提示必须指向设置页而不是让用户复制模板文件()
    {
        var session = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CloudFlow.Azure", "Auth", "MsalAccountSessionManager.cs"));
        Assert.DoesNotContain("appsettings.example.json", session, StringComparison.Ordinal);
        Assert.Contains("设置 → 账户 → 登录服务", session, StringComparison.Ordinal);
    }

    private static string AppDirectory() => Path.Combine(RepositoryRoot(), "src", "CloudFlow.App");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位源码。");
    }
}
