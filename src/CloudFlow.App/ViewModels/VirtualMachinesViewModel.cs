using System.Windows;
using System.Collections.ObjectModel;
using CloudFlow.App.Converters;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 虚拟机列表页（概念图 1 右窗口）：
/// 搜索 / 筛选 / 分页（10 条/页）/ 行操作（重启 / 关机 / 解除分配）。
/// </summary>
public partial class VirtualMachinesViewModel : ObservableObject
{
    /// <summary>中文筛选选项 → 状态键（颜色转换器仍用英文键）。</summary>
    private static readonly Dictionary<string, string> StatusFilterMap = new()
    {
        ["运行中"] = "Running",
        ["已停止"] = "Stopped",
        ["已解除分配"] = "Deallocated",
        ["警告"] = "Warning"
    };

    private readonly IVmInventoryService _inventory;
    private readonly IVmPowerService _power;
    private readonly ScopeContext _scopeContext;
    private readonly IShellNavigation _navigation;

    private List<VmSummary> _allVms = [];
    private List<VmSummary> _filtered = [];

    /// <summary>每页条数（概念图分页下拉 10/25/50）。</summary>
    private int _pageSize = 10;

    public IReadOnlyList<string> PageSizeOptions { get; } = ["10 条/页", "25 条/页", "50 条/页"];

    [ObservableProperty]
    private string _selectedPageSize = "10 条/页";

    partial void OnSelectedPageSizeChanged(string value)
    {
        var number = value.Split(' ')[0];
        if (int.TryParse(number, out var size) && size > 0)
        {
            _pageSize = size;
        }
        CurrentPage = 1;
        ApplyFilters();
    }

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private string _statusFilter = "全部状态";

    [ObservableProperty]
    private string _resourceGroupFilter = "全部资源组";

    [ObservableProperty]
    private string _regionFilter = "全部区域";

    public IReadOnlyList<string> StatusOptions { get; } =
        ["全部状态", .. StatusFilterMap.Keys];

    public IReadOnlyList<string> ResourceGroupOptions { get; private set; } = ["全部资源组"];

    public IReadOnlyList<string> RegionOptions { get; private set; } = ["全部区域"];

    [ObservableProperty]
    private ObservableCollection<VmSummary> _pageItems = [];

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _pageInfoText = "";

    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private string? _infoSeverity;

    [ObservableProperty]
    private bool _isLoading;

    public VirtualMachinesViewModel(
        IVmInventoryService inventory,
        IVmPowerService power,
        ScopeContext scopeContext,
        IShellNavigation navigation,
        IJobStore jobStore)
    {
        _inventory = inventory;
        _power = power;
        _scopeContext = scopeContext;
        _navigation = navigation;

        _scopeContext.ScopeChanged += OnScopeChanged;
        jobStore.JobChanged += OnJobChanged;
    }

    /// <summary>Shell 全局搜索跳转入口。</summary>
    public void SetExternalFilter(string filter)
    {
        FilterText = filter;
        CurrentPage = 1;
        ApplyFilters();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            _allVms = [.. await _inventory.QueryAsync(_scopeContext.CurrentScope)];
            ResourceGroupOptions = ["全部资源组", .. _allVms.Select(vm => vm.ResourceGroupName).Distinct().OrderBy(x => x)];
            RegionOptions = ["全部区域", .. _allVms.Select(vm => vm.Region).Distinct().OrderBy(x => x)];
            OnPropertyChanged(nameof(ResourceGroupOptions));
            OnPropertyChanged(nameof(RegionOptions));
            ApplyFilters();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnStatusFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnResourceGroupFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnRegionFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        IEnumerable<VmSummary> query = _allVms;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText.Trim();
            query = query.Where(vm =>
                vm.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                vm.ResourceGroupName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (vm.PublicIp?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                vm.VmSize.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (StatusFilter is not "全部状态" and not null and not "")
        {
            query = StatusFilterMap.TryGetValue(StatusFilter, out var key)
                ? (key == "Warning"
                    ? query.Where(vm => vm.HasWarning)
                    : query.Where(vm => vm.PowerState.ToString() == key))
                : query;
        }

        if (ResourceGroupFilter.StartsWith("全部") is false)
        {
            query = query.Where(vm => vm.ResourceGroupName == ResourceGroupFilter);
        }

        if (RegionFilter.StartsWith("全部") is false)
        {
            query = query.Where(vm => vm.Region == RegionFilter);
        }

        _filtered = [.. query.OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)];

        TotalPages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }

        var skip = (CurrentPage - 1) * _pageSize;
        PageItems = [.. _filtered.Skip(skip).Take(_pageSize)];

        var from = _filtered.Count == 0 ? 0 : skip + 1;
        var to = Math.Min(skip + _pageSize, _filtered.Count);
        PageInfoText = $"显示 {from}–{to}，共 {_filtered.Count} 台虚拟机";
        OnPropertyChanged(nameof(PageNumbers));
    }

    /// <summary>分页按钮数字（最多显示 7 页）。</summary>
    public IReadOnlyList<int> PageNumbers =>
        [.. Enumerable.Range(1, TotalPages).Take(7)];

    [RelayCommand]
    private void GoToPage(int page)
    {
        if (page is >= 1 && page <= TotalPages)
        {
            CurrentPage = page;
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            GoToPage(CurrentPage - 1);
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            GoToPage(CurrentPage + 1);
        }
    }

    [RelayCommand]
    private void OpenDetail(VmSummary vm) => _navigation.NavigateToVmDetail(vm);

    /// <summary>创建虚拟机（概念图保留按钮；预配能力不在 P1 运维范围，设计文档 §75/§80）。</summary>
    [RelayCommand]
    private void CreateVm() =>
        MessageBox.Show(
            "创建虚拟机属于预配（Provisioning）能力，不属于 P1 运维范围（设计文档 §75/§80）。\n按钮按 UI 概念图保留，将在后续版本实现。",
            "创建虚拟机", MessageBoxButton.OK, MessageBoxImage.Information);

    // ==== 行操作：全部经 Operation Engine（设计文档 §29）====

    [RelayCommand]
    private async Task StartAsync(VmSummary vm) => await SubmitPowerAsync(
        () => _power.StartAsync(vm), $"启动虚拟机 {vm.Name}");

    [RelayCommand]
    private async Task RestartAsync(VmSummary vm) => await SubmitPowerAsync(
        () => _power.RestartAsync(vm), $"重启虚拟机 {vm.Name}");

    [RelayCommand]
    private async Task PowerOffAsync(VmSummary vm)
    {
        var confirmed = MessageBox.Show(
            "关机\n\n虚拟机将停止，但计算资源仍保留分配。\n费用可能继续产生。\n\n是否继续？",
            "关机", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.PowerOffAsync(vm), $"关机 {vm.Name}");
    }

    [RelayCommand]
    private async Task DeallocateAsync(VmSummary vm)
    {
        var confirmed = MessageBox.Show(
            "停止并解除分配\n\n虚拟机将停止并释放计算资源。\n停止计算计费（磁盘与保留 IP 可能继续计费）。\n\n是否继续？",
            "解除分配", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.DeallocateAsync(vm), $"解除分配 {vm.Name}");
    }

    private async Task SubmitPowerAsync(Func<Task<OperationJob>> submit, string display)
    {
        InfoText = $"正在提交：{display}…";
        InfoSeverity = "Validating";
        try
        {
            var job = await submit();
            ShowJob(job);
        }
        catch (Exception ex)
        {
            InfoText = $"{display} 失败：{ex.Message}";
            InfoSeverity = "Failed";
        }
    }

    private void ShowJob(OperationJob job)
    {
        InfoSeverity = job.Status.ToString();
        InfoText = job.Status switch
        {
            JobStatus.Succeeded => $"{job.Display} —— 成功（已验证）。",
            JobStatus.Failed => $"{job.Display} —— 失败：{job.Error}",
            JobStatus.WaitingApproval => $"{job.Display} —— 等待审批。",
            _ => $"{job.Display} —— {CfStatusTextConverter.Map(job.Status.ToString())}…"
        };
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Job 完成后刷新行状态（Mock Handler 已改内存状态）
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed)
            {
                ShowJob(job);
                ApplyFilters();
            }
        });
    }
}
