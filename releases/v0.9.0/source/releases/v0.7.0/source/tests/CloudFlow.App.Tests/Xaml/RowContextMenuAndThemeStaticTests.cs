using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 行右键菜单与深色模式菜单样式的静态护栏。App 测试项目不引用 WPF，按
/// <see cref="CreateVmWizardStaticTests"/> 的做法直接核对源码约定。
/// </summary>
public sealed class RowContextMenuAndThemeStaticTests
{
    [Fact]
    public void 所有资源列表整行右键必须用LoadingRow逐行赋值菜单且虚拟机行不挂菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml"));
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml.cs"));

        // 之前用 DataGrid.RowStyle 的 Setter 让所有行共用同一个 ContextMenu 实例，行不
        // 虚拟化时几乎同时生成的多行会一起抢这份实例的逻辑父级归属，真实复现过第一条记录
        // 右键第一次没反应。改为 LoadingRow 逐行各自 FindResource 一次，拿到各自独立的实例。
        Assert.Contains("LoadingRow=\"AllResourcesGrid_LoadingRow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGrid.RowStyle>", xaml, StringComparison.Ordinal);

        Assert.Contains("AllResourcesGrid_LoadingRow", code, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"Cf.AllResourcesRowMenu\")", code, StringComparison.Ordinal);
        // 虚拟机有自己专门的删除流程（"⋯"按钮对这类行直接隐藏），整行右键不能绕过去，
        // LoadingRow 里必须按 IsVirtualMachine 把这类行的 ContextMenu 置空。
        Assert.Contains("IsVirtualMachine", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 资源组列表整行右键必须用LoadingRow逐行赋值菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml.cs"));

        Assert.Contains("LoadingRow=\"ResourceGroupsGrid_LoadingRow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGrid.RowStyle>", xaml, StringComparison.Ordinal);

        Assert.Contains("ResourceGroupsGrid_LoadingRow", code, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"Cf.ResourceGroupRowMenu\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 虚拟机列表整行右键也必须用LoadingRow逐行赋值菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "VirtualMachinesPage.xaml"));
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "VirtualMachinesPage.xaml.cs"));

        // 同一套坑理论上也存在于虚拟机列表页（默认开着行虚拟化，没那么容易复现，
        // 但代码模式一样，一并改掉）。
        Assert.Contains("LoadingRow=\"VmsGrid_LoadingRow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGrid.RowStyle>", xaml, StringComparison.Ordinal);

        Assert.Contains("VmsGrid_LoadingRow", code, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"Cf.VmRowMenu\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 深色模式下菜单及菜单项与分隔线必须自定义模板不留系统默认的浅色占位列()
    {
        var theme = File.ReadAllText(Path.Combine(AppDirectory(), "Themes", "CloudFlowTheme.xaml"));

        // 真实反馈：深色模式下右键菜单左边一条竖着的白条。第一次只重写了 MenuItem 的模板，
        // 白条依旧在——真正的占位列其实在 ContextMenu 自己的默认模板里（经典 Aero2 主题给
        // 整个菜单预留的一条贯穿全高的图标背景带），必须连 ContextMenu 一起重写模板。
        var contextMenuStart = theme.IndexOf("<Style TargetType=\"ContextMenu\">", StringComparison.Ordinal);
        Assert.True(contextMenuStart >= 0, "未找到 ContextMenu 的隐式样式。");
        var contextMenuEnd = theme.IndexOf("</Style>", contextMenuStart, StringComparison.Ordinal);
        var contextMenuBlock = theme[contextMenuStart..contextMenuEnd];

        Assert.Contains("ControlTemplate TargetType=\"ContextMenu\"", contextMenuBlock, StringComparison.Ordinal);
        Assert.Contains("ItemsPresenter", contextMenuBlock, StringComparison.Ordinal);

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
