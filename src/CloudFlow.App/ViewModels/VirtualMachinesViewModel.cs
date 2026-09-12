using System.Windows;
using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// Virtual Machines 列表页（概念图 1 右窗口）：
/// 搜索 / 筛选 / 分页（10 per page）/ 行操作（Restart / Shut down / Deallocate）。
/// </summary>
public partial class VirtualMachinesViewModel : ObservableObject
{
    private const int DefaultPageSize = 10;

    private readonly IVmInventoryService _inventory;
    private readonly IVmPowerService _power;
    private readonly ScopeContext _scopeContext;
    private readonly IShellNavigation _navigation;

    private List<VmSummary> _allVms = [];
    private List<VmSummary> _filtered = [];

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private string _statusFilter = "All statuses";

    [ObservableProperty]
    private string _resourceGroupFilter = "All resource groups";

    [ObservableProperty]
    private string _regionFilter = "All regions";

    public IReadOnlyList<string> StatusOptions { get; } =
        ["All statuses", "Running", "Stopped", "Deallocated", "Warning"];

    public IReadOnlyList<string> ResourceGroupOptions { get; private set; } = ["All resource groups"];

    public IReadOnlyList<string> RegionOptions { get; private set; } = ["All regions"];

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
            ResourceGroupOptions = ["All resource groups", .. _allVms.Select(vm => vm.ResourceGroupName).Distinct().OrderBy(x => x)];
            RegionOptions = ["All regions", .. _allVms.Select(vm => vm.Region).Distinct().OrderBy(x => x)];
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

        if (StatusFilter is not "All statuses" and not null and not "")
        {
            query = StatusFilter == "Warning"
                ? query.Where(vm => vm.HasWarning)
                : query.Where(vm => vm.PowerState.ToString() == StatusFilter);
        }

        if (ResourceGroupFilter.StartsWith("All ") is false)
        {
            query = query.Where(vm => vm.ResourceGroupName == ResourceGroupFilter);
        }

        if (RegionFilter.StartsWith("All ") is false)
        {
            query = query.Where(vm => vm.Region == RegionFilter);
        }

        _filtered = [.. query.OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)];

        TotalPages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)DefaultPageSize));
        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }

        var skip = (CurrentPage - 1) * DefaultPageSize;
        PageItems = [.. _filtered.Skip(skip).Take(DefaultPageSize)];

        var from = _filtered.Count == 0 ? 0 : skip + 1;
        var to = Math.Min(skip + DefaultPageSize, _filtered.Count);
        PageInfoText = $"Showing {from}–{to} of {_filtered.Count} virtual machines";
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

    // ==== 行操作：全部经 Operation Engine（设计文档 §29）====

    [RelayCommand]
    private async Task RestartAsync(VmSummary vm) => await SubmitPowerAsync(
        () => _power.RestartAsync(vm), $"Restart VM {vm.Name}");

    [RelayCommand]
    private async Task PowerOffAsync(VmSummary vm)
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Shut down\n\nThe VM stops but compute resources remain allocated.\nCharges may continue.\n\n继续吗？",
            "Shut down VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.PowerOffAsync(vm), $"Shut down VM {vm.Name}");
    }

    [RelayCommand]
    private async Task DeallocateAsync(VmSummary vm)
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Stop & Deallocate\n\nThe VM stops and compute resources are released.\n停止计算计费（磁盘与 IP 保留可能继续计费）。\n\n继续吗？",
            "Deallocate VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.DeallocateAsync(vm), $"Deallocate VM {vm.Name}");
    }

    private async Task SubmitPowerAsync(Func<Task<OperationJob>> submit, string display)
    {
        InfoText = $"Submitting: {display}…";
        InfoSeverity = "Validating";
        try
        {
            var job = await submit();
            ShowJob(job);
        }
        catch (Exception ex)
        {
            InfoText = $"{display} failed: {ex.Message}";
            InfoSeverity = "Failed";
        }
    }

    private void ShowJob(OperationJob job)
    {
        InfoSeverity = job.Status.ToString();
        InfoText = job.Status switch
        {
            JobStatus.Succeeded => $"{job.Display} — succeeded (verified).",
            JobStatus.Failed => $"{job.Display} — failed: {job.Error}",
            JobStatus.WaitingApproval => $"{job.Display} — waiting approval.",
            _ => $"{job.Display} — {job.Status}…"
        };
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
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
