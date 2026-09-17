using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 「关于」分节的静态护栏。App 测试项目不引用 WPF，按 <see cref="CreateVmWizardStaticTests"/>
/// 的做法直接核对源码约定。
///
/// **背景**：原来「关于」只有一行版本号（还把"演示模式/已连接"跟版本拼在一句里），
/// 没有开发者与源码信息。规范后的信息表里每一行都是独立属性，改其一不动其它。
/// </summary>
public sealed class AboutSectionStaticTests
{
    [Fact]
    public void 应用身份必须携带开发者GitHub与源码仓库地址()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Infrastructure", "AppInfo.cs"));

        Assert.Contains("public const string DeveloperGitHub = \"enpiaohj\";", code, StringComparison.Ordinal);
        // 仓库地址必须由开发者名拼出，避免两处手写漂移
        Assert.Contains("public const string RepositoryUrl = $\"https://github.com/{DeveloperGitHub}/cloudflow\";",
            code, StringComparison.Ordinal);
        Assert.Contains("public const string LicenseName = \"GPL-3.0\";", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 版本与运行模式必须拆成独立属性_不得再拼回一句()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("public string ProductVersionText", code, StringComparison.Ordinal);
        Assert.Contains("public string RuntimeModeText", code, StringComparison.Ordinal);
        Assert.Contains("public string DeveloperName", code, StringComparison.Ordinal);
        Assert.Contains("public string RepositoryUrl", code, StringComparison.Ordinal);
        Assert.Contains("public string LicenseName", code, StringComparison.Ordinal);

        // 旧的"版本 · 模式"拼接句已拆分，不应再存在
        Assert.DoesNotContain("public string AppVersion", code, StringComparison.Ordinal);
        Assert.DoesNotContain("· 演示模式（模拟数据）", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 关于分节必须包含版本开发者源码许可证四行与隐私披露()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"));

        var start = xaml.IndexOf("ConverterParameter=about", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到「关于」分节。");
        var end = xaml.IndexOf("</Border>", start, StringComparison.Ordinal);
        var section = xaml[start..end];

        Assert.Contains("Binding ProductVersionText", section, StringComparison.Ordinal);
        Assert.Contains("Binding RuntimeModeText", section, StringComparison.Ordinal);
        Assert.Contains("Binding DeveloperName", section, StringComparison.Ordinal);
        Assert.Contains("Binding RepositoryUrl", section, StringComparison.Ordinal);
        Assert.Contains("Binding LicenseName", section, StringComparison.Ordinal);
        Assert.Contains("Binding NetworkDisclosure", section, StringComparison.Ordinal);
        // 超链接必须显式交给系统 Shell 处理，否则桌面应用里点了没反应
        Assert.Contains("RequestNavigate=\"OpenRepositoryLink_Click\"", section, StringComparison.Ordinal);
    }

    [Fact]
    public void 仓库链接必须以UseShellExecute方式打开()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml.cs"));

        Assert.Contains("UseShellExecute = true", code, StringComparison.Ordinal);
        Assert.Contains("RequestNavigateEventArgs", code, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位「关于」分节源码。");
    }
}
