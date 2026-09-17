using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// "资源"导航分组（资源组 / 所有资源两个子页——清理创建虚拟机流程留下的资源组/虚拟网络）的
/// 静态护栏。App 测试项目不引用 WPF，这里按 <see cref="CreateVmWizardStaticTests"/> 的做法
/// 直接核对源码约定。
/// </summary>
public sealed class ResourceGroupsPageStaticTests
{
    [Fact]
    public void 导航分组与两个子页必须注册()
    {
        var shell = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ShellViewModel.cs"));
        Assert.Contains("PageKey = \"resources\"", shell, StringComparison.Ordinal);
        Assert.Contains("Label = \"资源\"", shell, StringComparison.Ordinal);
        Assert.Contains("PageKey = \"resourcegroups\"", shell, StringComparison.Ordinal);
        Assert.Contains("Label = \"资源组\"", shell, StringComparison.Ordinal);
        Assert.Contains("PageKey = \"allresources\"", shell, StringComparison.Ordinal);
        Assert.Contains("Label = \"所有资源\"", shell, StringComparison.Ordinal);
        Assert.Contains("Current = ResourceGroups", shell, StringComparison.Ordinal);
        Assert.Contains("Current = AllResources", shell, StringComparison.Ordinal);

        var mainWindow = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "MainWindow.xaml"));
        Assert.Contains("vm:ResourceGroupsViewModel", mainWindow, StringComparison.Ordinal);
        Assert.Contains("views:ResourceGroupsPage", mainWindow, StringComparison.Ordinal);
        Assert.Contains("vm:AllResourcesViewModel", mainWindow, StringComparison.Ordinal);
        Assert.Contains("views:AllResourcesPage", mainWindow, StringComparison.Ordinal);

        var appXaml = File.ReadAllText(Path.Combine(AppDirectory(), "App.xaml.cs"));
        Assert.Contains("services.AddSingleton<ResourceGroupsViewModel>();", appXaml, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<AllResourcesViewModel>();", appXaml, StringComparison.Ordinal);
        Assert.Contains("CloudFlow.Modules.Network.Services.IResourceGroupService", appXaml, StringComparison.Ordinal);
        Assert.Contains("CloudFlow.Modules.Network.Services.ResourceGroupService", appXaml, StringComparison.Ordinal);
        Assert.Contains("CloudFlow.Modules.Network.Services.IResourceService", appXaml, StringComparison.Ordinal);
        Assert.Contains("CloudFlow.Modules.Network.Services.ResourceService", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 资源组页删除必须走三个点菜单不能是悬挂的红按钮()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml"));

        Assert.Contains("x:Name=\"ResourceGroupsGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RefreshCommand}\"", xaml, StringComparison.Ordinal);
        // 容易误点：删除入口必须先经过"⋯"这一步，不能是行内常驻可见的按钮。
        Assert.Contains("Cf.ResourceGroupRowMenu", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"删除资源组\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RowMenu_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Cf.DangerButton", xaml, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml.cs"));
        Assert.Contains("RowMenu.OpenFor(button)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Vm.DeleteResourceGroupCommand.Execute(row)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有资源页必须区分虚拟机行与可删除行且删除入口走三个点菜单()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml"));
        Assert.Contains("x:Name=\"AllResourcesGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("查看虚拟机", xaml, StringComparison.Ordinal);
        // 容易误点：删除入口必须先经过"⋯"这一步，不能是行内常驻可见的红按钮。
        Assert.Contains("Cf.AllResourcesRowMenu", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"删除资源\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Cf.DangerButton", xaml, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml.cs"));
        Assert.Contains("RowMenu.OpenFor(button)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Vm.DeleteResourceCommand.Execute(row)", codeBehind, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));
        // 虚拟机有自己专门的删除流程（清理挂载的网卡/磁盘），这里不能重新实现一遍删除。
        Assert.Contains("!IsDeleting && !IsVirtualMachine", viewModel, StringComparison.Ordinal);
        Assert.Contains("row.IsVirtualMachine", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void 列表只反映当前Scope不做启发式过滤()
    {
        var resourceGroups = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ResourceGroupsViewModel.cs"));
        // 已确认的产品决策：列出当前 Scope 下全部资源组，不按名称/规模做筛选。
        Assert.Contains("_catalog.GetAllAsync(subscriptionId, forceRefresh: true)", resourceGroups, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPlatformManagedResourceGroup", resourceGroups, StringComparison.Ordinal);

        var allResources = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));
        Assert.DoesNotContain("IsPlatformManagedResourceGroup", allResources, StringComparison.Ordinal);
    }

    [Fact]
    public void 删除资源组与删除单个资源都必须要求输入名称确认()
    {
        var resourceGroups = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ResourceGroupsViewModel.cs"));
        Assert.Contains("confirmText: row.Name", resourceGroups, StringComparison.Ordinal);

        var allResources = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));
        Assert.Contains("confirmText: row.Name", allResources, StringComparison.Ordinal);

        var dialog = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ImpactApprovalDialog.xaml.cs"));
        Assert.Contains("bool RequiresConfirmText", dialog, StringComparison.Ordinal);
        // 大小写敏感比对——不能让"随手打错还是通过了"。
        Assert.Contains(
            "string.Equals(ConfirmTextBox.Text, _confirmText, StringComparison.Ordinal)",
            dialog, StringComparison.Ordinal);
        Assert.Contains("ContinueButton.IsEnabled = !RequiresConfirmText", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void 批量删除必须合并确认输入确认文本且取消时作废待审批任务()
    {
        foreach (var file in new[] { "ResourceGroupsViewModel.cs", "AllResourcesViewModel.cs" })
        {
            var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", file));
            // 多项合并成一个确认框，仍要输入"删除 N …"才能继续，门槛不因批量而降低。
            Assert.Contains("confirmText: $\"删除 {waiting.Count}", viewModel, StringComparison.Ordinal);
            // 取消（或提交中途出错）时，本轮提交的待审批任务一并作废，不留一堆挂着的待审批任务。
            Assert.Contains("BatchDeletion.RejectPendingAsync", viewModel, StringComparison.Ordinal);
            // 引擎把失败记在 Job 上照常返回、不抛异常——只有真正成功才能从列表里移除。
            Assert.Contains("finished.Status == JobStatus.Succeeded", viewModel, StringComparison.Ordinal);
            Assert.Contains("job.Status == JobStatus.Succeeded", viewModel, StringComparison.Ordinal);
            Assert.Contains("row.IsChecked && row.CanDelete", viewModel, StringComparison.Ordinal);
        }

        foreach (var page in new[] { "ResourceGroupsPage.xaml", "AllResourcesPage.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", page));
            // Cf.DataGrid 只读：勾选必须 OneWay + 代码后置写回（TwoWay 不会提交）。
            Assert.Contains("IsChecked=\"{Binding IsChecked, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Click=\"RowCheck_Click\"", xaml, StringComparison.Ordinal);
            // 不能删的行（虚拟机、删除中）勾不上。
            Assert.Contains("IsEnabled=\"{Binding CanDelete}\"", xaml, StringComparison.Ordinal);
            // 批量删除按钮只在有勾选时出现，不是常驻按钮。
            Assert.Contains("Command=\"{Binding BatchDeleteCommand}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Visibility=\"{Binding HasChecked", xaml, StringComparison.Ordinal);
        }

        var dialog = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ImpactApprovalDialog.xaml.cs"));
        Assert.Contains("IReadOnlyList<OperationJob> jobs", dialog, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真实踩过的坑：批量确认框原来直接堆 Azure Resource ID（一长串
    /// <c>/subscriptions/.../providers/...</c> 路径），批量场景下完全没法读；且原来的批量执行
    /// 循环里"删成功一个就立刻从列表移除"在真实账户上删 3 个以上时崩过一次
    /// <c>InvalidOperationException：某个 ItemsControl 与它的项源不一致</c>
    /// （DataGrid 的 ItemContainerGenerator 与连续快速的 Remove 失步）。
    /// </summary>
    [Fact]
    public void 批量确认框显示人类可读的名称而非原始ResourceId_且整批跑完才一次性移除行()
    {
        var dialog = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ImpactApprovalDialog.xaml.cs"));
        Assert.Contains("IReadOnlyList<string> targetLabels", dialog, StringComparison.Ordinal);
        Assert.Contains("ResourceId = string.Join(Environment.NewLine, targetLabels)", dialog, StringComparison.Ordinal);

        foreach (var file in new[] { "ResourceGroupsViewModel.cs", "AllResourcesViewModel.cs" })
        {
            var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", file));
            // 传给对话框的是 JobPresentation.BatchTargetLabel 生成的人类可读标签，不是 item.Job.ResourceId。
            Assert.Contains(
                "JobPresentation.BatchTargetLabel(item.Row.Name, item.Row.Location)",
                viewModel, StringComparison.Ordinal);
            // 批量执行循环内不再逐项 Rows.Remove——收集到 succeededRows，跑完整批才一次性重建 Rows。
            Assert.DoesNotContain("succeededRows.Add(row);\n                    Rows.Remove", viewModel);
            Assert.Contains("succeededRows.Add(row);", viewModel, StringComparison.Ordinal);
            Assert.Contains("new ObservableCollection<", viewModel, StringComparison.Ordinal);
        }

        var presentation = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "JobPresentation.cs"));
        // 区域真实值（代码）必须留着，中文名只是加在后面方便读，顺序与详情页的
        // AzureRegionCatalog.DisplayNameWithCode（中文在前）刻意相反——这里要先核对"是不是这个真实区域"。
        Assert.Contains("public static string BatchTargetLabel(string name, string? location)", presentation, StringComparison.Ordinal);
        Assert.Contains("$\"{name}（{location} · {chineseName}）\"", presentation, StringComparison.Ordinal);
    }

    [Fact]
    public void 单资源删除必须拒绝虚拟机类型()
    {
        var handler = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Modules", "CloudFlow.Modules.Network", "Operations", "DeleteResourceHandler.cs"));
        Assert.Contains("microsoft.compute/virtualmachines", handler, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("请到「虚拟机」页删除", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void 资源组页支持按名称搜索加区域订阅两个下拉筛选且不得在集合变更事件里同步刷新过滤器()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ResourceGroupsViewModel.cs"));
        Assert.Contains("private string _searchText", viewModel, StringComparison.Ordinal);
        Assert.Contains("private string _selectedLocation", viewModel, StringComparison.Ordinal);
        Assert.Contains("private string _selectedSubscription", viewModel, StringComparison.Ordinal);
        Assert.Contains("CollectionViewSource.GetDefaultView(Rows)", viewModel, StringComparison.Ordinal);

        // 同 AllResourcesViewModel 的同一处教训：这个页面之前没有过滤器所以从没崩过，
        // 现在补上过滤器就必须一起补上"延后刷新"，否则会在这个页面重演同一个崩溃；
        // RefreshFilterOptions() 复位无效选中值时会级联调用 ApplyFilter()，必须一起延后。
        var start = viewModel.IndexOf("private void OnRowsCollectionChanged", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 OnRowsCollectionChanged，无法核对是否延后刷新过滤器。");
        var end = viewModel.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = viewModel[start..end];

        var beginInvokeIndex = body.IndexOf("Dispatcher.BeginInvoke(", StringComparison.Ordinal);
        Assert.True(beginInvokeIndex >= 0, "未找到 Dispatcher.BeginInvoke，过滤器刷新可能又变回同步调用。");

        // 用带分号的调用形式匹配，不用裸的 "ApplyFilter()"——上面解释这条规避的注释文本本身
        // 就会提到 "ApplyFilter()"，裸匹配会把注释也算成一次调用，误判失败。
        var beforeBeginInvoke = body[..beginInvokeIndex];
        Assert.DoesNotContain("ApplyFilter();", beforeBeginInvoke, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshFilterOptions();", beforeBeginInvoke, StringComparison.Ordinal);

        var deferredBody = body[beginInvokeIndex..];
        Assert.Contains("ApplyFilter();", deferredBody, StringComparison.Ordinal);
        Assert.Contains("RefreshFilterOptions();", deferredBody, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "ResourceGroupsPage.xaml"));
        Assert.Contains("Binding SearchText", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedLocation", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedSubscription", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"False\"", xaml, StringComparison.Ordinal);

        // 真实反馈过：只靠下拉自己的"全部 XX"默认值，一旦选中了具体的值，就看不出这个下拉
        // 是筛哪一列的了——每个下拉旁边必须有一个不随选中值变化的说明性文字。
        Assert.Contains("Text=\"区域\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"订阅\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有资源页支持按名称搜索加类型资源组区域三个下拉筛选()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));
        Assert.Contains("private string _searchText", viewModel, StringComparison.Ordinal);
        Assert.Contains("private string _selectedType", viewModel, StringComparison.Ordinal);
        Assert.Contains("private string _selectedResourceGroup", viewModel, StringComparison.Ordinal);
        Assert.Contains("private string _selectedLocation", viewModel, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "AllResourcesPage.xaml"));
        Assert.Contains("Binding SearchText", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedType", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedResourceGroup", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding SelectedLocation", xaml, StringComparison.Ordinal);

        // 真实反馈过：只靠下拉自己的"全部 XX"默认值，一旦选中了具体的值，就看不出这个下拉
        // 是筛哪一列的了——每个下拉旁边必须有一个不随选中值变化的说明性文字。
        Assert.Contains("Text=\"类型\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"资源组\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"区域\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有资源列表变更事件里不得同步刷新过滤器()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "AllResourcesViewModel.cs"));

        // 真实崩溃过：单个资源删除（Rows.Remove(row)）会触发"某个 ItemsControl 与它的项源不一致"。
        // 根因是 OnRowsCollectionChanged 在 Rows 自己的 CollectionChanged 分发过程中同步调用
        // ApplyFilter()，而 ApplyFilter() 会重新赋值 CollectionView.Filter、触发一次嵌套的
        // 同步 Refresh（Reset 通知），跟 DataGrid 的 ItemContainerGenerator 正在处理的那次变更
        // 打架。必须延后到当前事件分发完全结束之后再刷新；RefreshFilterOptions() 会级联调用
        // ApplyFilter()，必须跟 ApplyFilter() 一起延后，不能只延后其中一个。
        var start = viewModel.IndexOf("private void OnRowsCollectionChanged", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 OnRowsCollectionChanged，无法核对是否延后刷新过滤器。");
        var end = viewModel.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = viewModel[start..end];

        var beginInvokeIndex = body.IndexOf("Dispatcher.BeginInvoke(", StringComparison.Ordinal);
        Assert.True(beginInvokeIndex >= 0, "未找到 Dispatcher.BeginInvoke，过滤器刷新可能又变回同步调用。");

        // 用带分号的调用形式匹配，不用裸的 "ApplyFilter()"——上面解释这条规避的注释文本本身
        // 就会提到 "ApplyFilter()"，裸匹配会把注释也算成一次调用，误判失败。
        var beforeBeginInvoke = body[..beginInvokeIndex];
        Assert.DoesNotContain("ApplyFilter();", beforeBeginInvoke, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshFilterOptions();", beforeBeginInvoke, StringComparison.Ordinal);

        var deferredBody = body[beginInvokeIndex..];
        Assert.Contains("ApplyFilter();", deferredBody, StringComparison.Ordinal);
        Assert.Contains("RefreshFilterOptions();", deferredBody, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位资源页源码。");
    }
}
