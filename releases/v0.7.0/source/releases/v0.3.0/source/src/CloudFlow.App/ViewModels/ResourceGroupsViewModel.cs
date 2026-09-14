using System.Collections.ObjectModel;
using System.Windows;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 资源组列表的一行。<see cref="ResourceCount"/> 单独异步补齐（每个资源组各查一次 Resource Graph），
/// 列表先按名称/区域渲染，数量列在到达前显示"…"——不能等全部查完才第一次显示列表。
/// </summary>
public sealed partial class ResourceGroupRow(
    string name, string location, string subscriptionId, string subscriptionName) : ObservableObject
{
    public string Name { get; } = name;

    public string Location { get; } = location;

    public string SubscriptionId { get; } = subscriptionId;

    /// <summary>订阅显示名；取不到时退回订阅 ID（原来整列都是一串 GUID，读不出是哪个订阅）。</summary>
    public string SubscriptionName { get; } =
        string.IsNullOrWhiteSpace(subscriptionName) ? subscriptionId : subscriptionName;

    [ObservableProperty]
    private int? _resourceCount;

    public string ResourceCountText => ResourceCount is { } count ? count.ToString() : "…";

    [ObservableProperty]
    private bool _isDeleting;

    /// <summary>行内"删除"按钮的可用性——没有现成的反向 Bool 转换器，直接算好一个属性更简单。</summary>
    public bool CanDelete => !IsDeleting;

    partial void OnResourceCountChanged(int? value) => OnPropertyChanged(nameof(ResourceCountText));

    partial void OnIsDeletingChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
}

/// <summary>
/// "资源"页（设计文档 v3.2 §"资源"）：清理"创建虚拟机"流程按需新建、但删除虚拟机时刻意不
/// 连带删除的网络类残留——列出当前 Scope 下全部资源组，删除走 Operation Engine 同一条纪律
/// （Validate → Impact → Permission → Execute → Verify → Audit），不做资源组以下的细粒度管理。
/// </summary>
public partial class ResourceGroupsViewModel : ObservableObject
{
    private readonly IResourceGroupCatalog _catalog;
    private readonly IResourceGroupDeleteExecutor _deleteExecutor;
    private readonly IResourceGroupService _service;
    private readonly IOperationEngine _engine;
    private readonly OperationRequestFactory _requests;
    private readonly ScopeContext _scopeContext;
    private readonly IJobStore _jobStore;
    private readonly MockVmInventoryService _demoInventory;

    public ResourceGroupsViewModel(
        IResourceGroupCatalog catalog,
        IResourceGroupDeleteExecutor deleteExecutor,
        IResourceGroupService service,
        IOperationEngine engine,
        OperationRequestFactory requests,
        ScopeContext scopeContext,
        IJobStore jobStore,
        MockVmInventoryService demoInventory)
    {
        _catalog = catalog;
        _deleteExecutor = deleteExecutor;
        _service = service;
        _engine = engine;
        _requests = requests;
        _scopeContext = scopeContext;
        _jobStore = jobStore;
        _demoInventory = demoInventory;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    [ObservableProperty]
    private ObservableCollection<ResourceGroupRow> _rows = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>操作反馈：与虚拟机列表页同一个 InfoBar 插槽范式，可手动关闭。</summary>
    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private string? _infoSeverity;

    public bool HasRows => Rows.Count > 0;

    public string SummaryText => $"共 {Rows.Count} 个资源组";

    public string EmptyText => _scopeContext.ActiveAccount is null
        ? "演示模式下暂无资源组数据。"
        : "当前 Scope 内没有资源组。可以在顶栏切换到其他订阅后再查看。";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            Rows = [.. await LoadRowsAsync().ConfigureAwait(true)];
            NotifyListChanged();
        }
        finally
        {
            IsLoading = false;
        }

        // 数量列走 Resource Graph，逐个资源组各一次请求——放列表渲染之后，不堵住"先看到列表"。
        _ = LoadResourceCountsAsync([.. Rows]);
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private async Task<List<ResourceGroupRow>> LoadRowsAsync()
    {
        if (_scopeContext.ActiveAccount is null)
        {
            var demoVms = await _demoInventory.QueryAsync(new ResourceScope()).ConfigureAwait(true);
            return [.. demoVms
                .Where(vm => !string.IsNullOrWhiteSpace(vm.ResourceGroupName))
                .GroupBy(vm => vm.ResourceGroupName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ResourceGroupRow(
                    group.Key, group.First().Region ?? "", group.First().SubscriptionId,
                    group.First().SubscriptionName))
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];
        }

        var rows = new List<ResourceGroupRow>();
        foreach (var subscriptionId in ResolveSubscriptionIds())
        {
            var subscriptionName = _scopeContext.AvailableSubscriptions
                .FirstOrDefault(s => string.Equals(s.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
                ?.DisplayName ?? "";
            var groups = await _catalog.GetAllAsync(subscriptionId).ConfigureAwait(true);
            rows.AddRange(groups.Select(g => new ResourceGroupRow(g.Name, g.Location, subscriptionId, subscriptionName)));
        }

        return [.. rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// AllAccessible（"全部可访问订阅"）没有具体订阅 ID 列表——资源组是按订阅枚举的 ARM 资源，
    /// 与 Resource Graph 不同，没法一次查全部订阅，只能展开成 Scope 已发现的每个订阅各查一次。
    /// </summary>
    private IReadOnlyList<string> ResolveSubscriptionIds()
    {
        var scope = _scopeContext.CurrentScope;
        return scope.Mode is ScopeMode.SingleSubscription or ScopeMode.MultipleSubscriptions
            ? scope.SubscriptionIds
            : [.. _scopeContext.AvailableSubscriptions.Select(s => s.SubscriptionId)];
    }

    private async Task LoadResourceCountsAsync(IReadOnlyList<ResourceGroupRow> rows)
    {
        foreach (var row in rows)
        {
            var request = _requests.Create(
                ResourceGroupModule.OperationDelete, row.SubscriptionId,
                $"/subscriptions/{row.SubscriptionId}/resourceGroups/{row.Name}",
                $"查询资源组 {row.Name} 内资源");
            try
            {
                var contained = await _deleteExecutor.GetContainedResourcesAsync(request).ConfigureAwait(true);
                row.ResourceCount = contained.Count;
            }
            catch (Exception)
            {
                // 查询失败不影响列表其它行——数量列保留"…"，不是致命错误。
            }
        }
    }

    /// <summary>停在 WaitingApproval 的 Job（§25 Continue 入口）——一次只能有一个待审批的删除。</summary>
    [ObservableProperty]
    private OperationJob? _pendingApprovalJob;

    /// <summary>
    /// 行操作"删除"：资源组删除是级联删除，比删单台虚拟机破坏半径更大——
    /// 要求用户在 <see cref="Views.ImpactApprovalDialog"/> 里输入资源组名称本身以确认
    /// （对齐 Azure Portal 自己删资源组的交互），且 <c>CannotBypass = true</c> 保证
    /// 即使把设置里的审批档关掉也照样拦得住。
    /// </summary>
    [RelayCommand]
    private async Task DeleteResourceGroupAsync(ResourceGroupRow? row)
    {
        if (row is null || row.IsDeleting)
        {
            return;
        }

        row.IsDeleting = true;
        InfoText = $"正在分析删除资源组 {row.Name} 的影响…";
        InfoSeverity = "Validating";
        try
        {
            var job = await _service.DeleteAsync(row.SubscriptionId, row.Name).ConfigureAwait(true);
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

            if (dialog.ShowDialog() is true)
            {
                Rows.Remove(row);
                NotifyListChanged();
            }

            PendingApprovalJob = null;
        }
        catch (Exception ex)
        {
            InfoText = $"删除资源组 {row.Name} 失败：{ex.Message}";
            InfoSeverity = "Failed";
        }
        finally
        {
            row.IsDeleting = false;
        }
    }

    private async Task<Views.ApprovalSubmitOutcome> ApproveJobAsync(
        Guid jobId, ResourceGroupRow row, Action<string> onProgress, CancellationToken ct)
    {
        void OnProgressChanged(object? sender, OperationJob job)
        {
            if (job.JobId != jobId || string.IsNullOrEmpty(job.ProgressNote))
            {
                return;
            }

            // JobChanged 可能在非 UI 线程触发（JobStore 内部做真实异步 I/O）。
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

    /// <summary>手动关闭操作反馈横幅——与虚拟机列表页同一条理由：顶栏"任务进行中"徽标已经
    /// 承担了"正在跑的任务"这个职责，这条横幅只负责"最近一次操作的即时反馈"。</summary>
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
