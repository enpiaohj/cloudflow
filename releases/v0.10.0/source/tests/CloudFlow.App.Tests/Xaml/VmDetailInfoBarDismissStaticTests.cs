using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 虚拟机详情页操作反馈条"关闭"按钮的静态护栏。App 测试项目不引用 WPF，按
/// <see cref="CreateVmWizardStaticTests"/> 的做法直接核对源码约定。
///
/// **回归背景**：虚拟机列表 / 所有资源 / 资源组三个页面的操作反馈条早就有手动关闭按钮
/// （<c>DismissInfoCommand</c>），唯独虚拟机详情页没有——"更改端口"这类操作完成后的反馈
/// 会一直停在页面上，只能靠离开再回到这个 VM 才会被清掉，跟其余三个页面的体验不一致。
/// </summary>
public sealed class VmDetailInfoBarDismissStaticTests
{
    [Fact]
    public void 详情页视图模型必须有关闭反馈条命令且一并清掉待审批任务()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "VmDetailViewModel.cs"));

        var start = code.IndexOf("private void DismissInfo", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 VmDetailViewModel.DismissInfo。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        Assert.Contains("InfoText = null", body, StringComparison.Ordinal);
        Assert.Contains("InfoSeverity = null", body, StringComparison.Ordinal);
        // 这条反馈条同时驱动"批准执行/作废"两个按钮（虚拟机列表等三个页面没有这层耦合），
        // 关掉反馈条却留着 PendingApprovalJob 会变成按钮消失但状态还在的死角。
        Assert.Contains("PendingApprovalJob = null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 详情页操作反馈条必须包含关闭按钮()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "VmDetailPage.xaml"));

        Assert.Contains("Binding DismissInfoCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Dismiss16", xaml, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位详情页反馈条源码。");
    }
}
