using System.Collections.ObjectModel;
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

    /// <summary>按名称 / 类型 / 资源组 / 区域过滤（只过滤已加载的列表，不重新查询 Azure）。</summary>
    [ObservableProperty]
    private string _searchText = "";

    public bool HasRows => Rows.Count > 0;

    /// <summary>列表下方的计数：有关键字时说明"找到几项、一共几项"。</summary>
    public string SummaryText { get; private set; } = "";

    /// <summary>有数据但关键字一项都没匹配上——与"根本没有资源"是两种空态，文案不同。</summary>
    public bool IsFilteredEmpty { get; private set; }

    public string EmptyText => _scopeContext.ActiveAccount is null
        ? "演示模式下暂无资源数据。"
        : "当前 Scope 内没有任何资源。可以在顶栏切换到其他订阅后再查看。";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnRowsChanged(ObservableCollection<AllResourceRow> value) => ApplyFilter();

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

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        var view = CollectionViewSource.GetDefaultView(Rows);
        view.Filter = keyword.Length == 0 ? null : item => item is AllResourceRow row && Matches(row, keyword);

        var visible = keyword.Length == 0 ? Rows.Count : view.Cast<object>().Count();
        SummaryText = keyword.Length == 0
            ? $"共 {Rows.Count} 项资源"
            : $"找到 {visible} 项，共 {Rows.Count} 项资源";
        IsFilteredEmpty = keyword.Length > 0 && Rows.Count > 0 && visible == 0;
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    private static bool Matches(AllResourceRow row, string keyword) =>
        row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || row.TypeDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || row.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || row.ResourceGroupName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || row.Location.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || AzureRegionCatalog.DisplayName(row.Location).Contains(keyword, StringComparison.OrdinalIgnoreCase);

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
            var groups = await _catalog.GetAllAsync(subscriptionId).ConfigureAwait(true);
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
                job, (onProgress, ct) => ApproveJobAsync(job.JobId, onProgress, ct),
                confirmText: row.Name)
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() is true)
            {
                Rows.Remove(row);
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(EmptyText));
                ApplyFilter();
            }

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
        }
    }

    private async Task<Views.ApprovalSubmitOutcome> ApproveJobAsync(
        Guid jobId, Action<string> onProgress, CancellationToken ct)
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
            var job = await _engine.ApproveAsync(jobId, ct).ConfigureAwait(true);
            ShowJob(job);
            return Views.ApprovalSubmitOutcome.Ok();
        }
        catch (Exception ex)
        {
            return Views.ApprovalSubmitOutcome.Failed(ex.Message);
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
