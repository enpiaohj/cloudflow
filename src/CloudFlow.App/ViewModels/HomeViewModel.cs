using System.Globalization;
using System.Windows;
using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Azure.Cost;
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
    private readonly ICostService _costService;
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

    /// <summary>空列表要给出说明，否则会被误读为加载失败。</summary>
    public bool HasRecentOperations => RecentOperations.Count > 0;

    /// <summary>
    /// 「虚拟机」统计卡副标题。Demo 模式沿用概念图的"本周新增 3 台"；
    /// 真实账户下没有创建时间数据，不编造增量，留空由 UI 折叠。
    /// </summary>
    public string TotalVmsDeltaText => _scopeContext.ActiveAccount is null ? "本周新增 3 台" : "";

    /// <summary>「需要关注」统计卡副标题：来自真实注意项数量，不再写死。</summary>
    public string AttentionSummary => _scopeContext.ActiveAccount is null
        ? "1 台意外停机"
        : AttentionItems.Count == 0 ? "未发现异常" : $"{AttentionItems.Count} 项待处理";

    public bool HasAttentionItems => AttentionItems.Count > 0;

    public string RecentOperationsEmptyText => _scopeContext.ActiveAccount is null
        ? "演示模式下暂无操作记录。"
        : "对资源执行启动、重启、快照或端口变更后，操作记录会显示在这里。";

    /// <summary>
    /// 注意项目前只按虚拟机电源状态判断（健康检查与告警尚未接入），
    /// 所以空态只陈述"没有电源状态异常"，不说"一切正常"——那是这里判断不了的结论。
    /// </summary>
    public string AttentionEmptyText => _scopeContext.ActiveAccount is null
        ? "演示模式下暂无注意项。"
        : "当前没有处于异常电源状态的虚拟机。";

    [ObservableProperty]
    private string _costAmount = "—";

    [ObservableProperty]
    private string _costSubtitle = "本月成本";

    /// <summary>首页每次显示都会立即刷新并覆盖这句；初值只在第一次读取完成前可见。</summary>
    [ObservableProperty]
    private string _costDelta = "正在读取成本数据…";

    /// <summary>是否有真实成本数据。没有时 UI 用中性色，避免把"尚未接入"显示成正面结论。</summary>
    [ObservableProperty]
    private bool _hasCostData;

    [ObservableProperty]
    private bool _isLoading;

    private bool _demoJobsSeeded;

    /// <summary>
    /// 冷启动、切换 Scope、切回首页三条路径都会各自触发一次 RefreshAsync，短时间内可能重叠
    /// （比如切换账户时 Scope 变了两次）。用同一个 Task 让重叠调用等已经在跑的那次完成，
    /// 而不是并发跑两次互相覆盖对方还没写完的统计数字。
    /// </summary>
    private Task? _refreshInFlight;

    public HomeViewModel(
        IVmInventoryService inventory,
        IJobStore jobStore,
        ICostService costService,
        ScopeContext scopeContext,
        IShellNavigation navigation)
    {
        _inventory = inventory;
        _jobStore = jobStore;
        _costService = costService;
        _scopeContext = scopeContext;
        _navigation = navigation;

        _jobStore.JobChanged += OnJobChanged;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        if (_refreshInFlight is { IsCompleted: false } inFlight)
        {
            return inFlight;
        }

        var task = RefreshCoreAsync();
        _refreshInFlight = task;
        return task;
    }

    private async Task RefreshCoreAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshDataAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshDataAsync()
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

        // 只显示当前账户的操作记录：混入 Demo / 其他账户的 Job 会被当成当前订阅发生过的事
        RecentOperations = [.. ActiveAccountJobs.For(_scopeContext, _jobStore.GetAll()).Take(6)];
        OnPropertyChanged(nameof(HasRecentOperations));
        OnPropertyChanged(nameof(RecentOperationsEmptyText));

        BuildAttentionItems(vms);
        OnPropertyChanged(nameof(HasAttentionItems));
        OnPropertyChanged(nameof(AttentionEmptyText));
        OnPropertyChanged(nameof(AttentionSummary));
        OnPropertyChanged(nameof(TotalVmsDeltaText));

        await UpdateCostInsightAsync();
    }

    /// <summary>
    /// 成本洞察（设计文档 Home）。
    /// 拿不到数据时显示 "—" 并给出**真实原因**（限流 / 无权限），
    /// 不用 0 或旧数字填充 —— 那会被当成当前账单。
    /// </summary>
    private async Task UpdateCostInsightAsync()
    {
        // 先切到中性态再发查询：Cost Management 限流时退避要几十秒，
        // 这段窗口里若还显示字段初始化的演示金额，用户会把它当成真实账单。
        if (_scopeContext.ActiveAccount is not null)
        {
            CostAmount = "—";
            CostSubtitle = "本月成本";
            CostDelta = "正在读取 Azure Cost Management…";
            HasCostData = false;
        }

        var subscriptionId = _scopeContext.CurrentScope.SubscriptionIds.FirstOrDefault()
            ?? _scopeContext.AvailableSubscriptions.FirstOrDefault()?.SubscriptionId
            ?? "";

        var (summary, failureReason) = await _costService.GetMonthToDateAsync(subscriptionId);

        if (summary is null)
        {
            CostAmount = "—";
            CostSubtitle = "本月成本";
            CostDelta = failureReason ?? "成本数据不可用。";
            HasCostData = false;
            return;
        }

        CostAmount = FormatAmount(summary);
        CostSubtitle = $"本月成本（{summary.PeriodText}）";
        CostDelta = $"数据时间 {summary.RetrievedAt:HH:mm}";
        HasCostData = true;
    }

    /// <summary>按币种格式化：金额与币种分开保存，显示时才拼，避免各处硬编码 "$"。</summary>
    private static string FormatAmount(CostSummary summary) => summary.Currency switch
    {
        "USD" => $"${summary.Amount:N2}",
        "CNY" => $"¥{summary.Amount:N2}",
        _ => $"{summary.Amount:N2} {summary.Currency}"
    };

    /// <summary>
    /// 注意项。
    /// Demo 模式给出与概念图 1 一致的示例；
    /// 真实账户下只从已读到的 VM 数据推导，绝不编造 VM 名或不存在的状态
    /// （伪造条目会被当成真实告警，且点击后跳到不存在的资源）。
    /// 真实实现应来自 Health / Alerts，属 P3。
    /// </summary>
    private void BuildAttentionItems(IReadOnlyList<VmSummary> vms)
    {
        AttentionItems = [];

        if (_scopeContext.ActiveAccount is not null)
        {
            foreach (var vm in vms.Where(vm => vm.HasWarning))
            {
                AttentionItems.Add(new AttentionItem
                {
                    Severity = "Error",
                    Title = vm.Name,
                    Description = "虚拟机状态异常",
                    Age = "",
                    VmName = vm.Name
                });
            }

            // 已停止但未解除分配的 VM：计算资源仍保留分配，费用继续产生，是真实可操作的注意项
            foreach (var vm in vms.Where(vm => vm.PowerState == VmPowerState.Stopped))
            {
                AttentionItems.Add(new AttentionItem
                {
                    Severity = "Warning",
                    Title = vm.Name,
                    Description = "已停止但未解除分配，计算资源仍计费",
                    Age = "",
                    VmName = vm.Name
                });
            }

            return;
        }

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

    /// <summary>
    /// Demo 历史操作（概念图 Recent Operations 表）。
    /// 仅在未登录（Demo 模式）时写入：登录真实账户后仍往 Job 库塞演示记录，
    /// 会让"任务 / 最近操作"看起来有数据但全是假的。
    /// </summary>
    private async Task SeedDemoJobsAsync()
    {
        if (_demoJobsSeeded || _jobStore.GetAll().Count > 0)
        {
            _demoJobsSeeded = true;
            return;
        }
        _demoJobsSeeded = true;

        if (_scopeContext.ActiveAccount is not null)
        {
            return;
        }

        var account = DemoIdentity.AccountId;
        var tenant = DemoIdentity.TenantId;
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
            RecentOperations = [.. ActiveAccountJobs.For(_scopeContext, _jobStore.GetAll()).Take(6)];
            OnPropertyChanged(nameof(HasRecentOperations));
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

    // ==== 快捷操作（概念图 1 四按钮）====
    // 创建虚拟机 / 创建快照都需要先选目标虚拟机（快照还要选目标磁盘），首页没有这个上下文，
    // 所以统一导航到虚拟机列表，由用户在列表/详情页里完成 —— 不在这里重复一份创建向导或桩提示。

    [RelayCommand]
    private void QuickCreateVm() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickOpenPort() => _navigation.NavigateVirtualMachines();

    [RelayCommand]
    private void QuickViewAllVms() => _navigation.NavigateVirtualMachines();
}
