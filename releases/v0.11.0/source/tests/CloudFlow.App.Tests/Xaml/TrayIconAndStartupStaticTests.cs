using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 系统托盘图标 + 开机启动 / 关闭到托盘设置的静态护栏。App 测试项目不引用 WPF，按
/// <see cref="CreateVmWizardStaticTests"/> 的做法直接核对源码约定。
/// </summary>
public sealed class TrayIconAndStartupStaticTests
{
    [Fact]
    public void 项目文件必须开启WindowsForms且移除冲突的隐式全局using()
    {
        var csproj = File.ReadAllText(Path.Combine(AppDirectory(), "CloudFlow.App.csproj"));

        // UseWPF 和 UseWindowsForms 同时打开时，SDK 会给两边各自的隐式全局 using 都加上，
        // UserControl / ComboBox / Application / Brush / Size / Point 这些类型名在两个
        // 命名空间里同时存在，不移除就直接编译失败。
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", csproj, StringComparison.Ordinal);
        Assert.Contains("<Using Remove=\"System.Windows.Forms\" />", csproj, StringComparison.Ordinal);
        Assert.Contains("<Using Remove=\"System.Drawing\" />", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsStartupManager必须只读写当前用户的Run注册表项()
    {
        var code = File.ReadAllText(
            Path.Combine(AppDirectory(), "Infrastructure", "WindowsStartupManager.cs"));

        // 只读写 HKCU，不碰 HKLM——自启动是"这个用户想不想要"的个人偏好，
        // 不该需要管理员权限才能勾/取消这一个设置页开关。
        Assert.Contains("Registry.CurrentUser", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.LocalMachine", code, StringComparison.Ordinal);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Run", code, StringComparison.Ordinal);
        Assert.Contains("IsRegistered", code, StringComparison.Ordinal);
        Assert.Contains("SetRegistered", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 应用启动时必须注册WindowsStartupManager()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "App.xaml.cs"));

        Assert.Contains("AddSingleton<CloudFlow.App.Infrastructure.WindowsStartupManager>()", code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 主窗口必须创建托盘图标且关闭窗口按设置决定最小化还是真正退出()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("NotifyIcon", code, StringComparison.Ordinal);
        Assert.Contains("ContextMenuStrip", code, StringComparison.Ordinal);

        // 托盘常驻，不受"最小化到托盘"这条设置影响——它只决定"关闭窗口"这一个动作的行为。
        Assert.Contains("_trayIcon?.Dispose()", code, StringComparison.Ordinal);

        // OnClosing 必须同时看 _isExiting 和 MinimizeToTrayOnClose：前者是"真的要退出"
        // 的唯一豁免口子，缺了就会把托盘"退出"菜单也拦成打不死的假退出。
        var start = code.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 OnClosing。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        Assert.Contains("_isExiting", body, StringComparison.Ordinal);
        Assert.Contains("MinimizeToTrayOnClose", body, StringComparison.Ordinal);
        Assert.Contains("HideToTray()", body, StringComparison.Ordinal);

        // 托盘"退出"必须先置位 _isExiting 再触发 Shutdown，否则会被上面那条拦截规则
        // 当成"用户点了×"重新藏回托盘。
        var exitStart = code.IndexOf("private void ExitFromTray", StringComparison.Ordinal);
        Assert.True(exitStart >= 0, "未找到 ExitFromTray。");
        var exitEnd = code.IndexOf("\n    }", exitStart, StringComparison.Ordinal);
        var exitBody = code[exitStart..exitEnd];
        Assert.Contains("_isExiting = true", exitBody, StringComparison.Ordinal);
    }

    [Fact]
    public void 设置页视图模型必须有开机启动与最小化到托盘两个属性且新增通用分节()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("private bool _launchAtStartup", code, StringComparison.Ordinal);
        Assert.Contains("private bool _minimizeToTrayOnClose", code, StringComparison.Ordinal);
        Assert.Contains("new(\"general\", \"通用\"", code, StringComparison.Ordinal);

        // 开机启动的真实生效状态必须以注册表（WindowsStartupManager）为准，不能只信一份
        // 落盘在 settings.json 里的记忆值——两者可能因为用户在系统"启动"设置页里
        // 关掉它而不一致。
        Assert.Contains("_startup.IsRegistered()", code, StringComparison.Ordinal);
        Assert.Contains("_startup.SetRegistered(value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 设置页必须包含开机启动与最小化到托盘两个开关及说明文字()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"));

        Assert.Contains("Binding LaunchAtStartup", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding MinimizeToTrayOnClose", xaml, StringComparison.Ordinal);
        Assert.Contains("登录 Windows 后自动运行。", xaml, StringComparison.Ordinal);
        Assert.Contains("关闭主窗口时，程序将最小化到系统托盘，方便快速访问。", xaml, StringComparison.Ordinal);
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
