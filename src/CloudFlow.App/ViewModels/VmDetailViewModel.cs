using System.Windows;
using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// VM 详情页（概念图 2）：Header + Overview / Network / Disks / Performance / Activity 标签页。
/// Network 标签是 P1 核心特色：聚合 NIC / VNet / Subnet / NSG + Inbound Rules（§20/§21）。
/// </summary>
public partial class VmDetailViewModel : ObservableObject
{
    private readonly IVmPowerService _power;
    private readonly IVmNetworkService _network;
    private readonly ICurrentIpProvider _currentIp;
    private readonly IJobStore _jobStore;
    private readonly IOperationEngine _engine;
    private readonly MockAccountContext _account;
    private readonly IShellNavigation _navigation;

    private VmSummary _vm = null!;
    private VmNetworkContext? _networkContext;

    /// <summary>Azure Resource ID（统一主键，供复制 / 审计引用）。</summary>
    public string ResourceId { get; private set; } = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _vmSize = "";

    [ObservableProperty]
    private string _region = "";

    [ObservableProperty]
    private string _computerName = "";

    [ObservableProperty]
    private string? _publicIp;

    [ObservableProperty]
    private string? _privateIp;

    [ObservableProperty]
    private string _subscriptionName = "";

    [ObservableProperty]
    private string _resourceGroupName = "";

    [ObservableProperty]
    private string _osTypeText = "";

    // ---- Network ----
    [ObservableProperty]
    private bool _isLoadingNetwork;

    [ObservableProperty]
    private string _nicName = "";

    [ObservableProperty]
    private string _vnetName = "";

    [ObservableProperty]
    private string _subnetText = "";

    [ObservableProperty]
    private string _nsgName = "";

    [ObservableProperty]
    private bool _isSharedSubnetNsg;

    [ObservableProperty]
    private string _securityState = "Protected";

    [ObservableProperty]
    private ObservableCollection<NsgSecurityRule> _inboundRules = [];

    // ---- Disks（Demo 数据）----
    public IReadOnlyList<DiskInfo> Disks { get; } =
    [
        new("osdisk-web01", "OS Disk", "128 GB", "Premium SSD (P10)", "Attached"),
        new("data-01", "Data Disk", "256 GB", "Standard SSD (E10)", "Attached")
    ];

    // ---- Performance（Demo 数据，概念：§27 不伪造真实 Azure 数据，Demo 明确标注）----
    [ObservableProperty]
    private string _cpuText = "12%";

    [ObservableProperty]
    private string _networkInText = "1.2 Mbps";

    [ObservableProperty]
    private string _networkOutText = "0.8 Mbps";

    [ObservableProperty]
    private string _diskReadText = "2.1 MB/s";

    [ObservableProperty]
    private string _diskWriteText = "1.4 MB/s";

    // ---- Activity ----
    [ObservableProperty]
    private ObservableCollection<OperationJob> _activityJobs = [];

    // ---- 操作反馈 ----
    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private string? _infoSeverity;

    /// <summary>处于 WaitingApproval 的 Job（内联审批按钮）。</summary>
    [ObservableProperty]
    private OperationJob? _pendingApprovalJob;

    public VmDetailViewModel(
        IVmPowerService power,
        IVmNetworkService network,
        ICurrentIpProvider currentIp,
        IJobStore jobStore,
        IOperationEngine engine,
        MockAccountContext account,
        IShellNavigation navigation)
    {
        _power = power;
        _network = network;
        _currentIp = currentIp;
        _jobStore = jobStore;
        _engine = engine;
        _account = account;
        _navigation = navigation;
        _engine.JobUpdated += OnEngineJobUpdated;
    }

    public void Initialize(VmSummary vm)
    {
        _vm = vm;
        ResourceId = vm.ResourceId;
        Name = vm.Name;
        StatusText = vm.StatusText;
        VmSize = vm.VmSize;
        Region = vm.Region;
        ComputerName = vm.ComputerName;
        PublicIp = vm.PublicIp;
        PrivateIp = vm.PrivateIp;
        SubscriptionName = vm.SubscriptionName;
        ResourceGroupName = vm.ResourceGroupName;
        OsTypeText = vm.OsType == VmOsType.Windows ? "Windows" : "Linux";
        InfoText = null;
        PendingApprovalJob = null;

        _ = LoadNetworkAsync();
        RefreshActivity();
    }

    public sealed record DiskInfo(string Name, string Type, string Size, string Tier, string Status);

    private async Task LoadNetworkAsync()
    {
        IsLoadingNetwork = true;
        try
        {
            var ctx = await _network.GetForVmAsync(_vm.ResourceId);
            _networkContext = ctx;
            if (ctx is not null)
            {
                NicName = ctx.NicName;
                VnetName = ctx.VnetName;
                SubnetText = $"{ctx.SubnetName} ({ctx.SubnetCidr})";
                NsgName = ctx.NsgName;
                IsSharedSubnetNsg = ctx.IsSharedSubnetNsg;
                SecurityState = ctx.SecurityState;
                InboundRules = [.. ctx.InboundRules.OrderBy(r => r.Priority)];
            }
            else
            {
                InboundRules = [];
            }
        }
        finally
        {
            IsLoadingNetwork = false;
        }
    }

    private void RefreshActivity()
    {
        ActivityJobs = [.. _jobStore.GetAll()
            .Where(j => string.Equals(j.ResourceId, _vm.ResourceId, StringComparison.OrdinalIgnoreCase))
            .Take(20)];
    }

    // ==== 电源操作 ====

    [RelayCommand]
    private async Task RestartAsync()
    {
        var job = await _power.RestartAsync(_vm);
        ShowJob(job);
    }

    [RelayCommand]
    private async Task PowerOffAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Shut down\n\nThe VM stops but compute resources remain allocated.\nCharges may continue.\n\n继续吗？",
            "Shut down VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        var job = await _power.PowerOffAsync(_vm);
        ShowJob(job);
    }

    [RelayCommand]
    private async Task DeallocateAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Stop & Deallocate\n\nThe VM stops and compute resources are released.\n\n继续吗？",
            "Deallocate VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }
        var job = await _power.DeallocateAsync(_vm);
        ShowJob(job);
    }

    // ==== Port Manager（§21–§24）====

    [RelayCommand]
    private async Task OpenPortAsync()
    {
        var ip = await _currentIp.GetCurrentIpAsync();
        var cidr = await _currentIp.GetCurrentIpCidrAsync();
        var dialog = new Views.OpenPortDialog(ip, cidr)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() is not true || dialog.Result is null)
        {
            return;
        }

        var result = dialog.Result;
        // Source=Any（Internet 暴露）时交给 Operation Engine 审批流（§25 原则）
        var preApproved = result.SourcePrefix is not ("*" or "0.0.0.0/0");

        var job = await _engine.SubmitAsync(new OperationRequest
        {
            OperationType = "network.open_port",
            AccountId = _account.AccountId,
            TenantId = _account.TenantId,
            SubscriptionId = _vm.SubscriptionId,
            ResourceId = _vm.ResourceId,
            Risk = result.SourcePrefix is ("*" or "0.0.0.0/0") ? RiskLevel.High : RiskLevel.Medium,
            PreApproved = preApproved,
            Display = $"Open Port {result.Port}",
            Payload = new Dictionary<string, string>
            {
                ["ruleName"] = result.RuleName,
                ["port"] = result.Port.ToString(),
                ["protocol"] = result.Protocol,
                ["sourcePrefix"] = result.SourcePrefix,
                ["sourceDisplay"] = result.SourceDisplay,
                ["origin"] = result.Origin,
                ["priority"] = result.Priority.ToString()
            }
        });

        await AfterRuleJobAsync(job);
    }

    [RelayCommand]
    private async Task ChangePortAsync(NsgSecurityRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        var dialog = new Views.ChangePortDialog(rule)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        var newPort = dialog.NewPort;

        // 概念图 2：Change Port 对话框即用户确认（共享 NSG 警告已在页面横幅展示）
        var job = await _engine.SubmitAsync(new OperationRequest
        {
            OperationType = "network.change_port",
            AccountId = _account.AccountId,
            TenantId = _account.TenantId,
            SubscriptionId = _vm.SubscriptionId,
            ResourceId = _vm.ResourceId,
            Risk = RiskLevel.Medium,
            PreApproved = true,
            Display = $"Change Port {rule.Name}: {rule.DestinationPort} → {newPort}",
            Payload = new Dictionary<string, string>
            {
                ["ruleId"] = rule.RuleId,
                ["ruleName"] = rule.Name,
                ["port"] = newPort.ToString()
            }
        });

        await AfterRuleJobAsync(job);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(NsgSecurityRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            $"Delete rule '{rule.Name}' (port {rule.DestinationPort})?\n该操作会先经 Impact Analysis。",
            "Delete Rule", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        var job = await _engine.SubmitAsync(new OperationRequest
        {
            OperationType = "network.delete_rule",
            AccountId = _account.AccountId,
            TenantId = _account.TenantId,
            SubscriptionId = _vm.SubscriptionId,
            ResourceId = _vm.ResourceId,
            Risk = RiskLevel.Medium,
            PreApproved = true,
            Display = $"Delete Rule {rule.Name}",
            Payload = new Dictionary<string, string>
            {
                ["ruleId"] = rule.RuleId,
                ["ruleName"] = rule.Name
            }
        });

        await AfterRuleJobAsync(job);
    }

    /// <summary>WaitingApproval Job 的内联审批（§25 Continue）。</summary>
    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (PendingApprovalJob is null)
        {
            return;
        }
        var job = await _engine.ApproveAsync(PendingApprovalJob.JobId);
        PendingApprovalJob = null;
        await AfterRuleJobAsync(job);
    }

    private async Task AfterRuleJobAsync(OperationJob job)
    {
        ShowJob(job);
        if (job.Status == JobStatus.WaitingApproval)
        {
            PendingApprovalJob = job;
            return;
        }
        await LoadNetworkAsync();
        RefreshActivity();
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

    private void OnEngineJobUpdated(object? sender, OperationJob job)
    {
        if (!string.Equals(job.ResourceId, _vm.ResourceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ShowJob(job);
            RefreshActivity();
        });
    }

    // ==== 通用 ====

    /// <summary>Connect（设计文档 §28）：Windows → mstsc；Linux → 复制 SSH 命令。CloudFlow 不保存密码。</summary>
    [RelayCommand]
    private void Connect()
    {
        var target = PublicIp ?? PrivateIp;
        if (string.IsNullOrEmpty(target))
        {
            System.Windows.MessageBox.Show("该 VM 没有 IP 地址（未分配）。",
                "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_vm.OsType == VmOsType.Windows)
            {
                System.Diagnostics.Process.Start("mstsc", $"/v:{target}");
            }
            else
            {
                System.Windows.Clipboard.SetText($"ssh azureuser@{target}");
                System.Windows.MessageBox.Show($"已复制 SSH 命令：\nssh azureuser@{target}",
                    "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动连接失败：{ex.Message}", "Connect",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Create Snapshot（P1 Exit Gate 项，操作接入在下一迭代）。</summary>
    [RelayCommand]
    private void TakeSnapshot() =>
        System.Windows.MessageBox.Show(
            "Snapshot 操作属于 P1 Exit Gate（设计文档 §80），将在下一迭代实现（当前为 Demo 模式）。",
            "Create Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);

    [RelayCommand]
    private void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    [RelayCommand]
    private void BackToVms() => _navigation.NavigateVirtualMachines();

    /// <summary>供 code-behind 菜单使用：Deallocate。</summary>
    public async void DeallocateFromMenu()
    {
        await DeallocateCommand.ExecuteAsync(null);
    }
}
