using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// "新建入站/出站规则"对话框 Allow/Deny 的静态护栏。App 测试项目不引用 WPF，按
/// <see cref="CreateVmWizardStaticTests"/> 的做法直接核对源码约定。
///
/// **回归背景**：这个对话框此前没有"操作"这一项，新建的规则永远是 Allow——跟 Azure 门户
/// 自己的"新建规则"面板不一致，也没法用它建一条拒绝规则。
/// </summary>
public sealed class OpenPortActionStaticTests
{
    [Fact]
    public void 对话框必须暴露AllowDeny两个选项且默认Allow()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "OpenPortDialog.xaml.cs"));

        Assert.Contains("private const string AllowOption = \"允许 (Allow)\";", code, StringComparison.Ordinal);
        Assert.Contains("private const string DenyOption = \"拒绝 (Deny)\";", code, StringComparison.Ordinal);
        Assert.Contains("public string[] ActionOptions { get; } = [AllowOption, DenyOption];", code,
            StringComparison.Ordinal);
        Assert.Contains("private string _selectedAction = AllowOption;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 对话框XAML必须包含操作下拉()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "OpenPortDialog.xaml"));

        Assert.Contains("Binding ActionOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedAction", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 建议规则名必须按操作端口方向拼接而不是写死固定值()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "OpenPortDialog.xaml.cs"));

        var start = code.IndexOf("private string SuggestedRuleName", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 SuggestedRuleName。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        // 照 Azure 自己给内置规则起名的方式（AllowVnetInBound / DenyAllOutBound）拼接，
        // 不能再是一个跟这条规则毫无关系的固定值（此前是 "AppAccess"）。
        Assert.Contains("\"Deny\" : \"Allow\"", body, StringComparison.Ordinal);
        Assert.Contains("\"OutBound\" : \"InBound\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AppAccess", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenPortResult必须携带Action字段()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "OpenPortDialog.xaml.cs"));

        var start = code.IndexOf("public sealed record OpenPortResult", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 OpenPortResult。");
        var end = code.IndexOf(");", start, StringComparison.Ordinal);
        var body = code[start..end];

        Assert.Contains("NsgRuleAction Action", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 详情页提交时必须把Action写进payload()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "VmDetailViewModel.cs"));

        Assert.Contains("[\"action\"] = result.Action.ToString()", code, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位新建规则对话框源码。");
    }
}
