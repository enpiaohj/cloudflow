using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 顶栏拖拽移动窗口的静态护栏。App 测试项目不引用 WPF，按 <see cref="CreateVmWizardStaticTests"/>
/// 的做法直接核对源码约定。
///
/// **回归背景**：<c>IsInteractiveElement</c> 原来沿可视化树往上找、没有设终止边界，
/// 会一路走到 <see cref="System.Windows.Window"/> 本身——而 <c>Window</c> 继承自
/// <c>Control</c>，导致每次点击最终都会在链条顶端命中"是控件"，无论点哪里都判定为
/// "点在交互控件上"，窗口因此永远拖不动。修复方式是把查找范围限定在顶栏 Border（挂
/// 事件的 sender）为止，不再越界查到窗口。
/// </summary>
public sealed class MainWindowDragStaticTests
{
    [Fact]
    public void 顶栏拖拽的可交互性判断必须限定边界不能一路查到Window()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "MainWindow.xaml.cs"));

        var start = code.IndexOf("private static bool IsInteractiveElement", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 IsInteractiveElement，无法核对顶栏拖拽逻辑。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        // 必须接收一个边界参数，且循环条件里要用它来提前终止——否则会一路查到
        // Window（Window 继承自 Control），导致每次点击都被误判成"点在控件上"。
        Assert.Contains("DependencyObject boundary", body, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(node, boundary)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 顶栏鼠标按下处理器必须把顶栏自身作为边界传入()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "MainWindow.xaml.cs"));

        var start = code.IndexOf("private void TopBar_MouseLeftButtonDown", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 TopBar_MouseLeftButtonDown。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        Assert.Contains("IsInteractiveElement(source, topBar)", body, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位顶栏拖拽源码。");
    }
}
