using System.Globalization;
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
/// Home 页（概念图 1 左窗口）：统计卡 + 最近操作 + 注意项 + 成本洞察 + 快捷操作。
/// 遵循设计文档 §40。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");

    private readonly IVmInventoryService _inventory;
    private readonly IJobStore _jobStore;
    private readonly ScopeContext _scopeContext;
    private readonly IShellNavigation _navigation;

    [ObservableProperty]
    private string _todayText = DateTime.Now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

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
    private string _costDelta = "↓ 12% 对比上月";

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

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var scope = _scopeContext.CurrentScope;
        var vms = await _inventory.QueryAsync(scope);

        TotalVms = vms.Count;
        RunningVms = vms.Count(vm => vm.PowerState == VmPowerState.Running);
        DeallocatedVms = vms.Count(vm => vm.PowerState == VmPowerState.Deallocated);
        NeedAttention = vms.Count(vm => vm.HasWarning);
        RunningPercent = TotalVms == 0 ? "" : $"占总量 {RunningVms * 100 / TotalVms}%";
        DeallocatedPercent = TotalVms == 0 ? "" : $"占总量 {DeallocatedVms * 100 / TotalVms}%";
        ScopeSubtitle = scope.Describe();

        await SeedDemoJobsAsync();

        var recent = _jobStore.GetAll().Take(6);
        RecentOperations = [.. recent];

        BuildAttentionItems(vms);
    }

    /// <summary>注意项（Demo 数据与概念图 1 一致；真实实现来自 Health/Alerts，P3）。</summary>
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
                Description = "虚拟机意外停止",
                Age = "2 小时前",
                VmName = unexpected.Name
            });
        }

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Warning",
            Title = "OLD-SQL",
            Description = "已 14 天未备份",
            Age = "1 天前",
            VmName = "OLD-SQL"
        });

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Warning",
            Title = "WEB-LEGACY",
            Description = "使用旧版 VM 规格（D2_v3）",
            Age = "2 天前",
            VmName = "WEB-LEGACY"
        });

        AttentionItems.Add(new AttentionItem
        {
            Severity = "Info",
            Title = "3 台虚拟机",
            Description = "有可用更新",
            Age = "3 天前"
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
                Summary = status == JobStatus.Succeeded ? $"{op} 已验证。" : "启动请求被 Azure 拒绝。",
                Error = status == JobStatus.Failed ? "操作失败" : null
            };
        }

        await _jobStore.AddAsync(Job("vm.start", "WEB01", JobStatus.Succeeded, 96, "启动虚拟机"));
        await _jobStore.AddAsync(Job("vm.restart", "SQL01", JobStatus.Succeeded, 82, "重启虚拟机"));
        await _jobStore.AddAsync(Job("disk.snapshot", "DEV01", JobStatus.Succeeded, 68, "创建快照"));
        await _jobStore.AddAsync(Job("vm.power_off", "TEST01", JobStatus.Succeeded, 56, "关机"));
        await _jobStore.AddAsync(Job("network.open_port", "BASTION01", JobStatus.Succeeded, 37, "打开端口（3389）"));
        await _jobStore.AddAsync(Job("vm.start", "APP01", JobStatus.Failed, 143, "启动虚拟机"));
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecentOperations = [.. _jobStore.GetAll().Take(6)];
        });
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
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

    // ==== 快捷操作（概念图 1 四按钮；创建虚拟机属预配能力，P1 为运维范围，点击给出说明）====

    [RelayCommand]
    private void QuickCreateVm() =>
        MessageBox.Show(
            "创建虚拟机属于预配（Provisioning）能力，不属于 P1 运维范围（设计文档 §75/§80）。\n按钮按 UI 概念图保留，将在后续版本实现。",
            "创建虚拟机", MessageBoxButton.OK, MessageBoxImage.Information);

    [RelayCommand]
    private void QuickOpenPort() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickViewAllVms() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickTakeSnapshot() =>
        MessageBox.Show(
            "快照操作属于 P1 Exit Gate（设计文档 §80），将在下一迭代实现（当前为演示模式）。",
            "创建快照", MessageBoxButton.OK, MessageBoxImage.Information);
}
