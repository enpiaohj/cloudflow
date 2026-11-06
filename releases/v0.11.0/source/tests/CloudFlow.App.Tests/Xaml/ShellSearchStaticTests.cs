using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 顶栏全局搜索的静态护栏。App 测试项目不引用 WPF，按 <see cref="CreateVmWizardStaticTests"/>
/// 的做法直接核对源码约定。
///
/// **回归背景**：顶栏搜索框占位文字写的是"搜索资源、虚拟机…"，但 SearchAsync 原来
/// 无论输入什么关键字都会跳到"虚拟机"列表——搜资源组、存储账户这类非虚拟机资源时，
/// 会被导到一个必然搜不到东西的虚拟机列表，看起来像是"搜索坏了"。
/// </summary>
public sealed class ShellSearchStaticTests
{
    [Fact]
    public void 顶栏搜索必须导到覆盖全部资源类型的所有资源页而不是只导到虚拟机()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ShellViewModel.cs"));

        var start = viewModel.IndexOf("private Task SearchAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 SearchAsync，无法核对顶栏搜索逻辑。");
        var end = viewModel.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = viewModel[start..end];

        Assert.Contains("NavigateAllResources", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateVirtualMachines", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigateAllResources必须复位其余筛选并停止虚拟机页自动刷新()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ShellViewModel.cs"));

        var start = viewModel.IndexOf("public void NavigateAllResources", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 NavigateAllResources。");
        var end = viewModel.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = viewModel[start..end];

        Assert.Contains("Vms.SetPageActive(false)", body, StringComparison.Ordinal);
        Assert.Contains("AllResources.SetExternalFilter", body, StringComparison.Ordinal);
        Assert.Contains("AllResources.RefreshAsync()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有资源的外部筛选入口必须复位类型资源组区域三个下拉()
    {
        var code = File.ReadAllText(
            Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));

        var start = code.IndexOf("public void SetExternalFilter", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 AllResourcesViewModel.SetExternalFilter。");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];

        // 不复位的话，上次留在这个页面时选的筛选条件会悄悄限制这次搜索的结果，
        // 让用户以为顶栏搜索"找不到"。
        Assert.Contains("SearchText = filter", body, StringComparison.Ordinal);
        Assert.Contains("SelectedType = AllTypesOption", body, StringComparison.Ordinal);
        Assert.Contains("SelectedResourceGroup = AllResourceGroupsOption", body, StringComparison.Ordinal);
        Assert.Contains("SelectedLocation = AllLocationsOption", body, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位顶栏搜索源码。");
    }
}
