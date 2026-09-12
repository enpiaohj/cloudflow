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

/// <summary>首页注意项（概念图 1 Attention Items）。</summary>
public sealed class AttentionItem
{
    public string Severity { get; init; } = "Info"; // Error / Warning / Info

    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public string Age { get; init; } = "";

    /// <summary>关联 VM 名，点击跳详情（可空）。</summary>
    public string? VmName { get; init; }
}

/// <summary>
/// Home 页（概念图 1 左窗口）：统计卡 + Recent Operations + Attention Items + Cost Insight + Quick Actions。
/// 遵循设计文档 §40。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IVmInventoryService _inventory;
    private readonly IJobStore _jobStore;
    private readonly ScopeContext _scopeContext;
    private readonly IShellNavigation _navigation;

    [ObservableProperty]
    private string _todayText = DateTime.Now.ToString("dddd, MMMM d, yyyy");

    [ObservableProperty]
    private string _scopeSubtitle = "";

    [ObservableProperty]
    private int _totalVms;

    [ObservableProperty]
    private int _runningVms;

    [ObservableProperty]
    private int _deallocatedVms;

    [ObservableProperty]
    private int _needAttention;

    [ObservableProperty]
    private string _runningPercent = "";

    [ObservableProperty]
    private string _deallocatedPercent = "";

    [ObservableProperty]
    private ObservableCollection<OperationJob> _recentOperations = [];

    [ObservableProperty]
    private ObservableCollection<AttentionItem> _attentionItems = [];

    [ObservableProperty]
    private string _costAmount = "$482.21";

    [ObservableProperty]
    private string _costDelta = "↓ 12% vs. last month";

    private bool _demoJobsSeeded;

    public HomeViewModel(
        IVmInventoryService inventory,
        IJobStore jobStore,
        ScopeContext scopeContext,
        IShellNavigation navigation)
    {
        _inventory = inventory;
        _jobStore = jobStore;
        _scopeContext = scopeContext;
        _navigation = navigation;

        _jobStore.JobChanged += OnJobChanged;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    /// <summary>Attention Items（Demo 数据与概念图 1 一致；真实实现来自 Health/Alerts，P3）。</summary>
    private void BuildAttentionItems(IReadOnlyList<VmSummary> vms)
    {
        AttentionItems = [];

        var unexpected = vms.FirstOrDefault(vm => vm.Name == "DEV01");
        if (unexpected is not null)
        {
            AttentionItems.Add(new AttentionItem
            {
                Severity = "Error",
                Title = unexpected.Name,
                Description = "VM stopped unexpectedly",
                Age = "2 hours ago",
                VmName = unexpected.Name
            });
        }

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Warning",
            Title = "OLD-SQL",
            Description = "Not backed up in 14 days",
            Age = "1 day ago",
            VmName = "OLD-SQL"
        });

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Warning",
            Title = "WEB-LEGACY",
            Description = "Using older VM size (D2_v3)",
            Age = "2 days ago",
            VmName = "WEB-LEGACY"
        });

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Info",
            Title = "3 VMs",
            Description = "Have available updates",
            Age = "3 days ago"
        });
    }

    /// <summary>Demo 历史操作（概念图 Recent Operations 表）。真实接入后由持久化 Job 历史替代。</summary>
    private async Task SeedDemoJobsAsync()
    {
        if (_demoJobsSeeded || _jobStore.GetAll().Count > 0)
        {
            _demoJobsSeeded = true;
            return;
        }
        _demoJobsSeeded = true;

        var account = "demo-account";
        var tenant = MockAccountContext.DemoTenantId;
        var sub = MockVmInventoryService.SubProdChina;

        OperationJob Job(string op, string vmName, JobStatus status, int minutesAgo, string display)
        {
            var resourceId =
                $"/subscriptions/{sub}/resourceGroups/rg-demo/providers/Microsoft.Compute/virtualMachines/{vmName}";
            return new OperationJob
            {
                AccountId = account,
                TenantId = tenant,
                SubscriptionId = sub,
                ResourceId = resourceId,
                OperationType = op,
                Display = display,
                Status = status,
                Risk = RiskLevel.Low,
                CreatedAt = DateTime.Now.AddMinutes(-minutesAgo),
                CompletedAt = DateTime.Now.AddMinutes(-minutesAgo).AddSeconds(45),
                Summary = status == JobStatus.Succeeded ? $"{op} verified." : "Start VM request was rejected by Azure.",
                Error = status == JobStatus.Failed ? "Operation failed" : null
            };
        }

        await _jobStore.AddAsync(Job("vm.start", "WEB01", JobStatus.Succeeded, 96, "Start VM"));
        await _jobStore.AddAsync(Job("vm.restart", "SQL01", JobStatus.Succeeded, 82, "Restart VM"));
        await _jobStore.AddAsync(Job("disk.snapshot", "DEV01", JobStatus.Succeeded, 68, "Create Snapshot"));
        await _jobStore.AddAsync(Job("vm.power_off", "TEST01", JobStatus.Succeeded, 56, "Stop VM"));
        await _jobStore.AddAsync(Job("network.open_port", "BASTION01", JobStatus.Succeeded, 37, "Open Port (3389)"));
        await _jobStore.AddAsync(Job("vm.start", "APP01", JobStatus.Failed, 143, "Start VM"));
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecentOperations = [.. _jobStore.GetAll().Take(6)];
        });
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var scope = _scopeContext.CurrentScope;
        var vms = await _inventory.QueryAsync(scope);

        TotalVms = vms.Count;
        RunningVms = vms.Count(vm => vm.PowerState == VmPowerState.Running);
        DeallocatedVms = vms.Count(vm => vm.PowerState == VmPowerState.Deallocated);
        NeedAttention = vms.Count(vm => vm.HasWarning);
        RunningPercent = TotalVms == 0 ? "" : $"{RunningVms * 100 / TotalVms}% of total";
        DeallocatedPercent = TotalVms == 0 ? "" : $"{DeallocatedVms * 100 / TotalVms}% of total";
        ScopeSubtitle = scope.Describe();

        await SeedDemoJobsAsync();

        var recent = _jobStore.GetAll().Take(6);
        RecentOperations = [.. recent];

        BuildAttentionItems(vms);
    }

    [RelayCommand]
    private void OpenAttentionItem(AttentionItem item)
    {
        if (item.VmName is null)
        {
            return;
        }
        _navigation.NavigateVirtualMachines(item.VmName);
    }

    [RelayCommand]
    private void ViewAllJobs() => _navigation.NavigateJobs();

    // ==== Quick Actions（概念图 1；Create VM 不在 P1 范围，设计文档 §75/§80）====

    [RelayCommand]
    private void QuickOpenPort() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickViewAllVms() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickTakeSnapshot() =>
        System.Windows.MessageBox.Show(
            "Snapshot 操作属于 P1 Exit Gate（设计文档 §80），将在下一迭代实现。",
            "CloudFlow", MessageBoxButton.OK, MessageBoxImage.Information);
}
