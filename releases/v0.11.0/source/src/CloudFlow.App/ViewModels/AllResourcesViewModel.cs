using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// "所有资源"页的一行：单个 Azure 资源（不是资源组）。粒度比 <see cref="ResourceGroupRow"/> 细，
/// 虚拟机行不提供删除——虚拟机有自己专门的删除流程（清理挂载的网卡/磁盘），
/// 这里只给"查看虚拟机"跳转，不重复实现一遍删除。
/// </summary>
public sealed partial class AllResourceRow(
    string name, string type, string resourceId, string resourceGroupName,
    string subscriptionId, string location) : ObservableObject
{
    private readonly AzureResourceTypeCatalog.Entry _typeInfo = AzureResourceTypeCatalog.Describe(type);

    public string Name { get; } = name;

    /// <summary>Resource Graph 原始类型（如 microsoft.network/networkwatchers），界面上只放在 ToolTip 里。</summary>
    public string Type { get; } = type;

    public string ResourceId { get; } = resourceId;

    public string ResourceGroupName { get; } = resourceGroupName;

    public string SubscriptionId { get; } = subscriptionId;

    public string Location { get; } = location;

    public bool IsVirtualMachine =>
        string.Equals(Type, "microsoft.compute/virtualmachines", StringComparison.OrdinalIgnoreCase);

    /// <summary>类型列展示值：Azure 门户同款的中文类型名（见 <see cref="AzureResourceTypeCatalog"/>）。</summary>
    public string TypeDisplay => _typeInfo.DisplayName;

    public Wpf.Ui.Controls.SymbolRegular TypeSymbol => _typeInfo.Symbol;

    [ObservableProperty]
    private bool _isDeleting;

    /// <summary>批量删除的勾选状态。由页面代码后置写回——Cf.DataGrid 只读，TwoWay 不会提交。
    /// 虚拟机行的复选框随 <see cref="CanDelete"/> 禁用，勾不上。</summary>
    [ObservableProperty]
    private bool _isChecked;

    public bool CanDelete => !IsDeleting && !IsVirtualMachine;

    partial void OnIsDeletingChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
}

/// <summary>
/// "所有资源"页（设计文档 v3.2 §"资源"）：与"资源组"页同属"资源"导航分组，
/// 粒度更细——列出当前 Scope 下每个资源组内的每一件资源，可以单独删除某一件
/// （比如遗留的虚拟网络），不必连带删除整个资源组。
/// </summary>
public partial class AllResourcesViewModel : ObservableObject
{
    private readonly IResourceGroupCatalog _catalog;
    private readonly IResourceGroupDeleteExecutor _groupExecutor;
    private readonly IResourceService _service;
    private readonly IOperationEngine _engine;
    private readonly OperationRequestFactory _requests;
    private readonly ScopeContext _scopeContext;
    private readonly IJobStore _jobStore;
    private readonly IShellNavigation _navigation;
    private readonly MockVmInventoryService _demoInventory;

    public AllResourcesViewModel(
        IResourceGroupCatalog catalog,
        IResourceGroupDeleteExecutor groupExecutor,
        IResourceService service,
        IOperationEngine engine,
        OperationRequestFactory requests,
        ScopeContext scopeContext,
        IJobStore jobStore,
        IShellNavigation navigation,
        MockVmInventoryService demoInventory)
    {
        _catalog = catalog;
        _groupExecutor = groupExecutor;
        _service = service;
        _engine = engine;
        _requests = requests;
        _scopeContext = scopeContext;
        _jobStore = jobStore;
        _navigation = navigation;
        _demoInventory = demoInventory;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    [ObservableProperty]
    private ObservableCollection<AllResourceRow> _rows = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private string? _infoSeverity;

    // 各自带字段名而不是共用一个裸的"全部"：下拉框收起来时唯一可见的内容就是当前选中值，
    // 三个都显示"全部"会分不清哪个是哪个字段——真实反馈过这个问题。
    private const string AllTypesOption = "全部类型";
    private const string AllResourceGroupsOption = "全部资源组";
    private const string AllLocationsOption = "全部区域";

    /// <summary>按名称过滤（只过滤已加载的列表，不重新查询 Azure）；类型/资源组/区域走下面三个
    /// 下拉筛选器，各自精确匹配，与名称关键字一起按 AND 组合。</summary>
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedType = AllTypesOption;

    [ObservableProperty]
    private string _selectedResourceGroup = AllResourceGroupsOption;

    [ObservableProperty]
    private string _selectedLocation = AllLocationsOption;

    /// <summary>三个下拉筛选器的可选项：来自当前已加载 Rows 的去重值，各自的"全部 XX"固定在最前面
    /// 表示不筛选。数据每次变化都会重新计算——账户下资源变了，下拉里能选的值也要跟着变。</summary>
    public ObservableCollection<string> TypeOptions { get; } = [AllTypesOption];

    public ObservableCollection<string> ResourceGroupOptions { get; } = [AllResourceGroupsOption];

    public ObservableCollection<string> LocationOptions { get; } = [AllLocationsOption];

    public bool HasRows => Rows.Count > 0;

    /// <summary>列表下方的计数：有关键字时说明"找到几项、一共几项"。</summary>
    public string SummaryText { get; private set; } = "";

    /// <summary>有数据但关键字一项都没匹配上——与"根本没有资源"是两种空态，文案不同。</summary>
    public bool IsFilteredEmpty { get; private set; }

    public string EmptyText => _scopeContext.ActiveAccount is null
        ? "演示模式下暂无资源数据。"
        : "当前 Scope 内没有任何资源。可以在顶栏切换到其他订阅后再查看。";

    /// <summary>
    /// 已勾选的行数。换了搜索关键字后，之前勾选、现在被过滤掉的行仍算在内——确认框会逐项列出
    /// 全部目标，删的是什么一目了然，不会因为"看不见"而被悄悄删掉或悄悄漏掉。
    /// </summary>
    public int CheckedCount => Rows.Count(row => row.IsChecked);

    public bool HasChecked => CheckedCount > 0;

    public string BatchDeleteText => $"删除所选（{CheckedCount}）";

    /// <summary>表头全选框的显示状态：当前可见（未被搜索过滤）的可删除行都已勾选。</summary>
    public bool IsAllChecked
    {
        get
        {
            var deletable = VisibleDeletableRows();
            return deletable.Count > 0 && deletable.All(row => row.IsChecked);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>顶栏全局搜索入口用——按关键字过滤，并把类型/资源组/区域三个下拉复位到
    /// "全部"。不复位的话，上次留在这个页面时选的筛选条件会悄悄限制这次搜索的结果，
    /// 让用户以为顶栏搜索"找不到"。</summary>
    public void SetExternalFilter(string filter)
    {
        SearchText = filter;
        SelectedType = AllTypesOption;
        SelectedResourceGroup = AllResourceGroupsOption;
        SelectedLocation = AllLocationsOption;
    }

    partial void OnSelectedTypeChanged(string value) => ApplyFilter();

    partial void OnSelectedResourceGroupChanged(string value) => ApplyFilter();

    partial void OnSelectedLocationChanged(string value) => ApplyFilter();

    partial void OnRowsChanged(
        ObservableCollection<AllResourceRow>? oldValue, ObservableCollection<AllResourceRow> newValue)
    {
        if (oldValue is not null)
        {
            oldValue.CollectionChanged -= OnRowsCollectionChanged;
            foreach (var row in oldValue)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        newValue.CollectionChanged += OnRowsCollectionChanged;
        foreach (var row in newValue)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        // 先重建三个下拉的可选项，再筛选——RefreshFilterOptions 内部如果发现当前选中值已经不在
        // 新清单里，会把它复位成"全部"，这个复位本身会级联触发一次 ApplyFilter，属于安全时机
        // （原因见 OnRowsCollectionChanged 那处更详细的注释）。
        RefreshFilterOptions();
        ApplyFilter();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<AllResourceRow>() ?? [])
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        foreach (var row in e.NewItems?.OfType<AllResourceRow>() ?? [])
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));

        // 不能在这里同步调用 RefreshFilterOptions()/ApplyFilter()：ApplyFilter() 会给
        // CollectionView.Filter 重新赋值，触发一次同步的 Refresh（Reset 通知）。这个方法本身是
        // Rows 自己的 CollectionChanged 事件分发出来的，同一次分发里再嵌套抛出一次 Reset，会跟
        // DataGrid 的 ItemContainerGenerator 正在处理的那次变更打架——真实崩溃过：单个资源删除
        // （Rows.Remove(row)）就会触发"某个 ItemsControl 与它的项源不一致"（累积计数对不上）。
        // RefreshFilterOptions() 复位无效选中值时会级联调用 ApplyFilter()，所以两个必须一起延后，
        // 不能只延后其中一个。延后到当前这次事件分发完全结束之后再刷新，就不会再嵌套。
        // ResourceGroupsViewModel 的同名方法不受影响——那个页面没有筛选器，
        // OnRowsCollectionChanged 里不碰 CollectionView，不存在这个问题。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshFilterOptions();
            ApplyFilter();
        });
    }

    /// <summary>重建三个下拉筛选器的可选项（来自当前 Rows 的去重值）；当前选中值如果已经不在
    /// 新清单里（比如筛的那个资源组被删光了），复位成"全部"，避免下拉停在一个不存在的值上。</summary>
    private void RefreshFilterOptions()
    {
        UpdateOptions(TypeOptions, AllTypesOption, Rows.Select(row => row.TypeDisplay));
        UpdateOptions(ResourceGroupOptions, AllResourceGroupsOption, Rows.Select(row => row.ResourceGroupName));
        UpdateOptions(LocationOptions, AllLocationsOption, Rows.Select(row => AzureRegionCatalog.DisplayName(row.Location)));

        if (!TypeOptions.Contains(SelectedType))
        {
            SelectedType = AllTypesOption;
        }

        if (!ResourceGroupOptions.Contains(SelectedResourceGroup))
        {
            SelectedResourceGroup = AllResourceGroupsOption;
        }

        if (!LocationOptions.Contains(SelectedLocation))
        {
            SelectedLocation = AllLocationsOption;
        }
    }

    private static void UpdateOptions(ObservableCollection<string> options, string allOption, IEnumerable<string> values)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        options.Clear();
        options.Add(allOption);
        foreach (var value in distinct)
        {
            options.Add(value);
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AllResourceRow.IsChecked) or nameof(AllResourceRow.CanDelete))
        {
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(BatchDeleteText));
        OnPropertyChanged(nameof(IsAllChecked));
    }

    private List<AllResourceRow> VisibleDeletableRows() =>
        [.. CollectionViewSource.GetDefaultView(Rows).Cast<AllResourceRow>().Where(row => row.CanDelete)];

    /// <summary>表头全选框：只作用于当前可见的可删除行（虚拟机行不参与）。</summary>
    [RelayCommand]
    private void ToggleAllChecked()
    {
        var target = !IsAllChecked;
        foreach (var row in VisibleDeletableRows())
        {
            row.IsChecked = target;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            Rows = [.. await LoadRowsAsync().ConfigureAwait(true)];
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(EmptyText));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool HasActiveFilter =>
        SearchText.Trim().Length > 0 || SelectedType != AllTypesOption ||
        SelectedResourceGroup != AllResourceGroupsOption || SelectedLocation != AllLocationsOption;

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(Rows);
        view.Filter = HasActiveFilter ? item => item is AllResourceRow row && Matches(row) : null;

        var visible = HasActiveFilter ? view.Cast<object>().Count() : Rows.Count;
        SummaryText = HasActiveFilter
            ? $"找到 {visible} 项，共 {Rows.Count} 项资源"
            : $"共 {Rows.Count} 项资源";
        IsFilteredEmpty = HasActiveFilter && Rows.Count > 0 && visible == 0;
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        NotifySelectionChanged();
    }

    /// <summary>名称走关键字包含匹配；类型/资源组/区域走下拉的精确匹配，与名称按 AND 组合——
    /// 三个下拉各自代表一列，选中即精确匹配该列，不需要再做包含匹配。</summary>
    private bool Matches(AllResourceRow row)
    {
        var keyword = SearchText.Trim();
        if (keyword.Length > 0 && !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SelectedType != AllTypesOption &&
            !string.Equals(row.TypeDisplay, SelectedType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SelectedResourceGroup != AllResourceGroupsOption &&
            !string.Equals(row.ResourceGroupName, SelectedResourceGroup, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SelectedLocation != AllLocationsOption &&
            !string.Equals(AzureRegionCatalog.DisplayName(row.Location), SelectedLocation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<List<AllResourceRow>> LoadRowsAsync()
    {
        var groups = await LoadResourceGroupsAsync().ConfigureAwait(true);
        var rows = new List<AllResourceRow>();

        foreach (var (name, location, subscriptionId) in groups)
        {
            var request = _requests.Create(
                ResourceGroupModule.OperationDelete, subscriptionId,
                $"/subscriptions/{subscriptionId}/resourceGroups/{name}",
                $"查询资源组 {name} 内资源");
            IReadOnlyList<ResourceSummary> contained;
            try
            {
                contained = await _groupExecutor.GetContainedResourcesAsync(request).ConfigureAwait(true);
            }
            catch (Exception)
            {
                continue;
            }

            // "区域"列显示资源自己的区域；原来填的是资源组区域，
            // NetworkWatcher_koreacentral 这类资源会被标成 eastasia（资源组所在区域）。
            rows.AddRange(contained.Select(item => new AllResourceRow(
                item.Name, item.Type, item.Id, name, subscriptionId,
                string.IsNullOrWhiteSpace(item.Location) ? location : item.Location)));
        }

        return [.. rows.OrderBy(row => row.ResourceGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<List<(string Name, string Location, string SubscriptionId)>> LoadResourceGroupsAsync()
    {
        if (_scopeContext.ActiveAccount is null)
        {
            var demoVms = await _demoInventory.QueryAsync(new ResourceScope()).ConfigureAwait(true);
            return [.. demoVms
                .Where(vm => !string.IsNullOrWhiteSpace(vm.ResourceGroupName))
                .GroupBy(vm => vm.ResourceGroupName, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.Key, group.First().Region ?? "", group.First().SubscriptionId))];
        }

        var result = new List<(string, string, string)>();
        foreach (var subscriptionId in ResolveSubscriptionIds())
        {
            // forceRefresh：理由同 ResourceGroupsViewModel 的同一处调用。
            var groups = await _catalog.GetAllAsync(subscriptionId, forceRefresh: true).ConfigureAwait(true);
            result.AddRange(groups.Select(g => (g.Name, g.Location, subscriptionId)));
        }

        return result;
    }

    /// <summary>见 <see cref="ResourceGroupsViewModel"/> 的同名方法——AllAccessible 没有具体订阅 ID
    /// 列表，只能展开成 Scope 已发现的每个订阅各查一次。</summary>
    private IReadOnlyList<string> ResolveSubscriptionIds()
    {
        var scope = _scopeContext.CurrentScope;
        return scope.Mode is ScopeMode.SingleSubscription or ScopeMode.MultipleSubscriptions
            ? scope.SubscriptionIds
            : [.. _scopeContext.AvailableSubscriptions.Select(s => s.SubscriptionId)];
    }

    /// <summary>虚拟机行没有删除入口，只能跳转到虚拟机页——避免绕开专门的虚拟机删除流程。</summary>
    [RelayCommand]
    private void ViewVm(AllResourceRow? row)
    {
        if (row is not null)
        {
            _navigation.NavigateVirtualMachines(row.Name);
        }
    }

    [ObservableProperty]
    private OperationJob? _pendingApprovalJob;

    [RelayCommand]
    private async Task DeleteResourceAsync(AllResourceRow? row)
    {
        if (row is null || row.IsDeleting || row.IsVirtualMachine)
        {
            return;
        }

        row.IsDeleting = true;
        InfoText = $"正在分析删除资源 {row.Name} 的影响…";
        InfoSeverity = "Validating";
        try
        {
            var job = await _service.DeleteAsync(row.SubscriptionId, row.ResourceId, row.Name, row.Type)
                .ConfigureAwait(true);
            ShowJob(job);

            if (job.Status != JobStatus.WaitingApproval)
            {
                return;
            }

            PendingApprovalJob = job;
            var dialog = new Views.ImpactApprovalDialog(
                job, (onProgress, ct) => ApproveJobAsync(job.JobId, row, onProgress, ct),
                confirmText: row.Name)
            {
                Owner = Application.Current?.MainWindow
            };

            dialog.ShowDialog();
            PendingApprovalJob = null;
        }
        catch (Exception ex)
        {
            InfoText = $"删除资源 {row.Name} 失败：{ex.Message}";
            InfoSeverity = "Failed";
        }
        finally
        {
            row.IsDeleting = false;
            row.IsChecked = false;
        }
    }

    private async Task<Views.ApprovalSubmitOutcome> ApproveJobAsync(
        Guid jobId, AllResourceRow row, Action<string> onProgress, CancellationToken ct)
    {
        try
        {
            var job = await RunApprovalAsync(jobId, onProgress, ct).ConfigureAwait(true);
            ShowJob(job);

            // 流水线失败不抛异常——引擎把失败记在 Job 上照常返回。只有真正成功才从列表移除。
            if (job.Status == JobStatus.Succeeded)
            {
                Rows.Remove(row);
            }

            return Views.ApprovalSubmitOutcome.Ok();
        }
        catch (Exception ex)
        {
            return Views.ApprovalSubmitOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 批量删除勾选的资源（虚拟机行勾不上，不在其中）：逐个提交 → 合并成一个确认框（输入
    /// "删除 N 项资源"确认）→ 按依赖顺序逐个执行。每项仍是独立 Job、各自留审计记录。
    /// 只选了一项时退回单个删除（按名称确认）。
    /// </summary>
    [RelayCommand]
    private async Task BatchDeleteAsync()
    {
        var targets = Rows
            .Where(row => row.IsChecked && row.CanDelete)
            .OrderBy(DeletionOrder)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        if (targets.Count == 1)
        {
            await DeleteResourceAsync(targets[0]).ConfigureAwait(true);
            return;
        }

        var waiting = new List<(AllResourceRow Row, OperationJob Job)>();
        var notSubmitted = new List<string>();
        foreach (var row in targets)
        {
            row.IsDeleting = true;
        }

        InfoText = $"正在分析删除 {targets.Count} 项资源的影响…";
        InfoSeverity = "Validating";
        try
        {
            foreach (var row in targets)
            {
                var job = await _service.DeleteAsync(row.SubscriptionId, row.ResourceId, row.Name, row.Type)
                    .ConfigureAwait(true);
                if (job.Status == JobStatus.WaitingApproval)
                {
                    waiting.Add((row, job));
                }
                else
                {
                    notSubmitted.Add(JobPresentation.Feedback(job));
                }
            }

            if (waiting.Count == 0)
            {
                InfoText = $"未能提交批量删除：{string.Join("；", notSubmitted)}";
                InfoSeverity = "Failed";
                return;
            }

            var result = new BatchDeleteResult();
            var dialog = new Views.ImpactApprovalDialog(
                [.. waiting.Select(item => item.Job)],
                [.. waiting.Select(item => JobPresentation.BatchTargetLabel(item.Row.Name, item.Row.Location))],
                $"批量删除资源（{waiting.Count} 项）",
                (onProgress, ct) => ApproveBatchAsync(waiting, result, onProgress, ct),
                confirmText: $"删除 {waiting.Count} 项资源")
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() is true)
            {
                (InfoText, InfoSeverity) = result.Describe("项资源", notSubmitted);
            }
            else
            {
                await BatchDeletion.RejectPendingAsync(_engine, waiting.Select(item => item.Job)).ConfigureAwait(true);
                InfoText = $"已取消批量删除，本次提交的 {waiting.Count} 个待审批任务已作废。";
                InfoSeverity = nameof(JobStatus.Canceled);
            }
        }
        catch (Exception ex)
        {
            await BatchDeletion.RejectPendingAsync(_engine, waiting.Select(item => item.Job)).ConfigureAwait(true);
            InfoText = $"批量删除资源失败：{ex.Message}";
            InfoSeverity = "Failed";
        }
        finally
        {
            foreach (var row in targets)
            {
                row.IsDeleting = false;
                row.IsChecked = false;
            }
        }
    }

    /// <summary>
    /// 同一批里的删除顺序。网卡引用着公网 IP、网络安全组和子网，虚拟网络在还有网卡占用子网时
    /// 删不掉；网络安全组 / 路由表关联在子网上时也删不掉——所以先删网卡，再删其它资源，
    /// 然后删虚拟网络（连带解除子网上的关联），最后删网络安全组和路由表。
    /// 顺序不对的话，同一批里排在前面的资源会因为依赖必然失败。
    /// </summary>
    private static int DeletionOrder(AllResourceRow row) => row.Type.ToLowerInvariant() switch
    {
        "microsoft.network/networkinterfaces" => 0,
        "microsoft.network/virtualnetworks" => 2,
        "microsoft.network/networksecuritygroups" or "microsoft.network/routetables" => 3,
        _ => 1
    };

    /// <summary>
    /// 合并确认之后逐个执行。不并发，理由见 <see cref="ResourceGroupsViewModel"/> 的同名方法。
    /// </summary>
    /// <remarks>
    /// 真实踩过的坑：这里原来是每删成功一项就立刻 <c>Rows.Remove(row)</c>，在真实账户上批量
    /// 删除时崩过一次 <c>InvalidOperationException：某个 ItemsControl 与它的项源不一致</c>
    /// ——AllResourcesGrid 的 ItemContainerGenerator 在"await 让出线程 → 下一次 Remove"这种
    /// 节奏的连续快速删除下会跟 ObservableCollection 的实际状态失步。改为循环内只记录哪些
    /// 成功了，等整批跑完后一次性重建 <see cref="Rows"/>，DataGrid 只收到一次变更通知。
    /// </remarks>
    private async Task<Views.ApprovalSubmitOutcome> ApproveBatchAsync(
        IReadOnlyList<(AllResourceRow Row, OperationJob Job)> items, BatchDeleteResult result,
        Action<string> onProgress, CancellationToken ct)
    {
        var succeededRows = new List<AllResourceRow>();
        for (var i = 0; i < items.Count; i++)
        {
            var (row, job) = items[i];
            var prefix = $"第 {i + 1}/{items.Count} 项 · {row.Name}";
            onProgress($"{prefix}：正在删除…");
            try
            {
                var finished = await RunApprovalAsync(job.JobId, note => onProgress($"{prefix}：{note}"), ct)
                    .ConfigureAwait(true);
                if (finished.Status == JobStatus.Succeeded)
                {
                    result.Succeeded++;
                    succeededRows.Add(row);
                }
                else
                {
                    result.Failures.Add(JobPresentation.Feedback(finished));
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{row.Name}：{ex.Message}");
            }
        }

        // 整批跑完后一次性从列表移除，DataGrid 只收到一次变更通知（见本方法上方的注释）。
        if (succeededRows.Count > 0)
        {
            var remaining = Rows.Where(row => !succeededRows.Contains(row));
            Rows = new ObservableCollection<AllResourceRow>(remaining);
        }

        return Views.ApprovalSubmitOutcome.Ok();
    }

    /// <summary>批准一个待审批 Job，期间把它的子步骤进度转给调用方。</summary>
    private async Task<OperationJob> RunApprovalAsync(Guid jobId, Action<string> onProgress, CancellationToken ct)
    {
        void OnProgressChanged(object? sender, OperationJob job)
        {
            if (job.JobId != jobId || string.IsNullOrEmpty(job.ProgressNote))
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(() => onProgress(job.ProgressNote!));
        }

        _jobStore.JobChanged += OnProgressChanged;
        try
        {
            return await _engine.ApproveAsync(jobId, ct).ConfigureAwait(true);
        }
        finally
        {
            _jobStore.JobChanged -= OnProgressChanged;
        }
    }

    [RelayCommand]
    private void DismissInfo()
    {
        InfoText = null;
        InfoSeverity = null;
    }

    private void ShowJob(OperationJob job)
    {
        InfoSeverity = job.Status.ToString();
        InfoText = JobPresentation.Feedback(job);
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }
}
