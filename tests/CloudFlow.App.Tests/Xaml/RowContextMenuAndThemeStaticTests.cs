using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 行右键菜单与深色模式菜单样式的静态护栏。App 测试项目不引用 WPF，按
/// <see cref="CreateVmWizardStaticTests"/> 的做法直接核对源码约定。
/// </summary>
public sealed class RowContextMenuAndThemeStaticTests
{
    [Fact]
    public void 所有资源列表整行右键必须打开行菜单且虚拟机行不挂菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml"));

        // 之前只有"⋯"按钮能开菜单，整行右键没反应——补上 DataGrid.RowStyle 让右键也能用
        // （虚拟机列表页已经是这样，这里对齐）。
        var rowStyleStart = xaml.IndexOf("<DataGrid.RowStyle>", StringComparison.Ordinal);
        Assert.True(rowStyleStart >= 0, "未找到 AllResourcesGrid 的 DataGrid.RowStyle。");
        var rowStyleEnd = xaml.IndexOf("</DataGrid.RowStyle>", rowStyleStart, StringComparison.Ordinal);
        var rowStyleBlock = xaml[rowStyleStart..rowStyleEnd];

        Assert.Contains("Cf.AllResourcesRowMenu", rowStyleBlock, StringComparison.Ordinal);
        // 虚拟机有自己专门的删除流程（"⋯"按钮对这类行直接隐藏），整行右键不能绕过去，
        // 必须用 DataTrigger 把虚拟机行的 ContextMenu 置空。
        Assert.Contains("IsVirtualMachine", rowStyleBlock, StringComparison.Ordinal);
        Assert.Contains("ContextMenu\" Value=\"{x:Null}\"", rowStyleBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void 资源组列表整行右键必须打开行菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml"));

        var rowStyleStart = xaml.IndexOf("<DataGrid.RowStyle>", StringComparison.Ordinal);
        Assert.True(rowStyleStart >= 0, "未找到 ResourceGroupsGrid 的 DataGrid.RowStyle。");
        var rowStyleEnd = xaml.IndexOf("</DataGrid.RowStyle>", rowStyleStart, StringComparison.Ordinal);
        var rowStyleBlock = xaml[rowStyleStart..rowStyleEnd];

        Assert.Contains("Cf.ResourceGroupRowMenu", rowStyleBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void 深色模式下菜单项与分隔线必须自定义模板不留系统默认的浅色占位列()
    {
        var theme = File.ReadAllText(Path.Combine(AppDirectory(), "Themes", "CloudFlowTheme.xaml"));

        // 真实反馈：深色模式下右键菜单左边一条竖着的白条——WPF 默认 MenuItem 模板里的
        // 图标/勾选占位列有自己独立的浅色背景，只设 Foreground/Background 不重写模板盖不掉它。
        var menuItemStart = theme.IndexOf("<Style TargetType=\"MenuItem\">", StringComparison.Ordinal);
        Assert.True(menuItemStart >= 0, "未找到 MenuItem 的隐式样式。");
        var menuItemEnd = theme.IndexOf("</Style>", menuItemStart, StringComparison.Ordinal);
        var menuItemBlock = theme[menuItemStart..menuItemEnd];

        Assert.Contains("ControlTemplate TargetType=\"MenuItem\"", menuItemBlock, StringComparison.Ordinal);
        Assert.Contains("IsHighlighted", menuItemBlock, StringComparison.Ordinal);

        // Separator 默认模板同样是两条线的立体凹槽效果，深色模式下那条浅色高光线也会露白。
        var separatorStart = theme.IndexOf("<Style TargetType=\"Separator\">", StringComparison.Ordinal);
        Assert.True(separatorStart >= 0, "未找到 Separator 的隐式样式。");
        var separatorEnd = theme.IndexOf("</Style>", separatorStart, StringComparison.Ordinal);
        var separatorBlock = theme[separatorStart..separatorEnd];

        Assert.Contains("ControlTemplate TargetType=\"Separator\"", separatorBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void 顶栏账户名宽度必须能容纳常见邮箱长度的登录名()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "MainWindow.xaml"));

        // 真实反馈：piaohongji@live.cn 能完整显示、piaohongji@outlook.com 却被截断——
        // 原来的 MaxWidth="130" 装不下常见邮箱域名，且登录名（AccountDisplayName）现在固定
        // 显示的就是邮箱地址，不能再按"展示名通常很短"的旧假设留窄。
        var start = xaml.IndexOf("Binding AccountDisplayName", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到顶栏账户名的绑定。");
        var end = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        var block = xaml[start..end];

        Assert.DoesNotContain("MaxWidth=\"130\"", block, StringComparison.Ordinal);
        Assert.Contains("TextTrimming", block, StringComparison.Ordinal);
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
