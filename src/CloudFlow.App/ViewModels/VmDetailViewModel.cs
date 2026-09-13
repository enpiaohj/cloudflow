using System.Windows;
using System.Collections.ObjectModel;
using CloudFlow.App.Converters;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
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
    private readonly IVmDetailService _detailService;
    private readonly IVmDiskService _diskService;
    private readonly IVmMetricsService _metrics;
    private readonly ICurrentIpProvider _currentIp;
    private readonly IJobStore _jobStore;
    private readonly IOperationEngine _engine;
    private readonly OperationRequestFactory _requests;
    private readonly IApprovalPolicy _approvalPolicy;
    private readonly SshConnectionService _ssh;

    /// <summary>连接流程：从"用户选了哪条凭据"到"会话建起来"。详情页与列表共用这一份实现。</summary>
    private readonly SshConnectFlow _connectFlow;
    private readonly IShellNavigation _navigation;
    private readonly ScopeContext _scopeContext;

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

    /// <summary>
    /// 概览页的分组内容。由 <see cref="VmSummary"/>(清单) + <see cref="VmDetailInfo"/>(ARM 详情) 合成：
    /// 空值行在合成时就被丢掉，因此列表里不会出现 "—" 占位行（完整性监视是唯一例外，见下）。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<OverviewSection> _overviewSections = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicIpText))]
    private string? _publicIp;

    /// <summary>公网 IP 资源名（概念图 2：Public IP 行显示 "ip (资源名)"，便于在门户中定位）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicIpText))]
    private string? _publicIpName;

    /// <summary>公网 IP 展示文本：地址 +（资源名）。</summary>
    public string PublicIpText => string.IsNullOrEmpty(PublicIpName)
        ? PublicIp ?? ""
        : $"{PublicIp} ({PublicIpName})";

    /// <summary>
    /// 公网 IP 的 DNS 名称（如 <c>appscloud.koreacentral.cloudapp.azure.com</c>）。
    /// 只有配了 DNS 名称标签的公网 IP 才有值；为空时整行不显示 —— 这是常态，
    /// 不是"没读到"，所以不能渲染成 "—"。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublicIpFqdn))]
    private string? _publicIpFqdn;

    public bool HasPublicIpFqdn => !string.IsNullOrEmpty(PublicIpFqdn);

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

    /// <summary>安全状态徽章右侧的说明文字，随实际读到的状态变化。</summary>
    [ObservableProperty]
    private string _nsgStateText = "正在读取网络配置…";

    /// <summary>
    /// NSG 安全状态。默认值必须是"未知"而不是"受保护"：
    /// 数据还没读回来（或读取失败）时显示"受保护"，等于在没验证的情况下宣称这台机器安全。
    /// </summary>
    [ObservableProperty]
    private string _securityState = "Unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInboundRules))]
    private ObservableCollection<NsgSecurityRule> _inboundRules = [];

    /// <summary>
    /// 出站端口规则。与入站规则同源同表，只按方向分流 —— 分开渲染是因为两侧
    /// "用户填的那个地址"分别落在目标侧和来源侧，合成一张表会出现一半的列对一半的行没意义。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutboundRules))]
    private ObservableCollection<NsgSecurityRule> _outboundRules = [];

    /// <summary>
    /// 表格是否有内容。用它而不是直接绑 <c>Count</c>：空表格要显示一句"为什么是空的"，
    /// 而 Azure 的 <c>securityRules</c> 只含自定义规则，默认规则（AllowInternetOutBound 等）
    /// 不在其中，所以"出站规则为空"是常态，不是异常。
    /// </summary>
    public bool HasInboundRules => InboundRules.Count > 0;

    public bool HasOutboundRules => OutboundRules.Count > 0;

    // ---- 出站连接（设计决策 §11；微软出站连通性优先级）----
    // 这一组投影的唯一纪律：入口处就把"查不到"与"确定没有"分成两句不同的话。
    // 出口 IP 拿不到时渲染成 "—"，和真实存在的"无公网出口"看起来一模一样，
    // 用户会以为端口已经收紧 —— 而默认出站恰恰是"有出口但地址在微软手里"。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutbound))]
    private string _outboundTypeText = "";

    /// <summary>徽章配色键，交给 CfStatusBrushConverter 复用既有状态色。</summary>
    [ObservableProperty]
    private string _outboundBadge = "Unknown";

    /// <summary>出口 IP 文案。**绝不**在"地址不可得"时渲染成 "—" 或 "无"。</summary>
    [ObservableProperty]
    private string _outboundIpText = "";

    /// <summary>
    /// 出口 IP 的原始地址（可复制的那一份）。与入站的 <c>PublicIp</c> 严格分开 ——
    /// NAT Gateway / LB 的出口地址不是这台 VM 的入站地址（设计决策 §13）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOutboundIp))]
    private string? _outboundAddress;

    /// <summary>判定过程的一句话说明。卡片正文不显示，挂在方式徽章的 tooltip 上。</summary>
    [ObservableProperty]
    private string _outboundExplanation = "";

    /// <summary>逐块网卡的出站明细。多网卡时才是多行，单网卡时只用于展示所在子网。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutboundNicRows))]
    [NotifyPropertyChangedFor(nameof(HasMultipleOutboundNics))]
    private ObservableCollection<OutboundNicRow> _outboundNicRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutboundSuppressed))]
    private string _outboundSuppressedText = "";

    /// <summary>卡片是否有可显示的内容。判据是**徽章文字**，不是说明文字 —— 说明文字已不在正文里。</summary>
    public bool HasOutbound => !string.IsNullOrEmpty(OutboundTypeText);

    /// <summary>
    /// 出口 IP 是否是一个**可复制的真实地址**。用 <see cref="OutboundIp"/> 判空而不是判
    /// <see cref="OutboundIpText"/>：后者在"未知（ARM 无法获取）"时也非空，
    /// 照它显示复制按钮，用户复制到的是一句说明文字。
    /// </summary>
    public bool CanCopyOutboundIp => !string.IsNullOrWhiteSpace(OutboundAddress);

    /// <summary>
    /// 多网卡才逐块列出。单网卡时逐块块会与上面的 VM 级结论逐字重复 ——
    /// 那是同一份数据在同一张卡里出现两次，不是"更详细"。
    /// </summary>
    public bool HasMultipleOutboundNics => OutboundNicRows.Count > 1;

    public bool HasOutboundNicRows => OutboundNicRows.Count > 0;

    public bool HasOutboundSuppressed => !string.IsNullOrEmpty(OutboundSuppressedText);

    // ---- 磁盘（设计文档 §26；当前 Mock，P1 接入 ARM Disks / Snapshots）----
    [ObservableProperty]
    private ObservableCollection<VmDiskInfo> _disks = [];

    [ObservableProperty]
    private int _snapshotTotal;

    // ---- Performance（设计文档 §27：Azure Monitor 主机指标，实时读取，无数据时显示 "—"）----
    // 默认 "—" 而不是演示数字：性能页展示的是"这台机器现在多忙"，
    // 用 Mock 数值冒充会被当成真实读数。
    [ObservableProperty]
    private string _cpuText = "—";

    /// <summary>内存使用率（%）。Azure 只给「可用内存百分比」，已换算为使用率。</summary>
    [ObservableProperty]
    private string _memoryText = "—";

    // 以下四项为「消耗百分比」：相对该规格上限的使用率
    [ObservableProperty]
    private string _osDiskIopsText = "—";

    [ObservableProperty]
    private string _osDiskBandwidthText = "—";

    [ObservableProperty]
    private string _vmCachedIopsText = "—";

    [ObservableProperty]
    private string _vmCachedBandwidthText = "—";

    [ObservableProperty]
    private string _vmUncachedIopsText = "—";

    [ObservableProperty]
    private string _vmUncachedBandwidthText = "—";

    [ObservableProperty]
    private string _networkInText = "—";

    [ObservableProperty]
    private string _networkOutText = "—";

    [ObservableProperty]
    private string _diskReadText = "—";

    [ObservableProperty]
    private string _diskWriteText = "—";

    /// <summary>指标来源说明 / 数据时间。指标有分钟级延迟，必须标明，否则会被当成此刻的实时值。</summary>
    [ObservableProperty]
    private string _metricsNote = "正在读取 Azure Monitor 主机指标…";

    // ---- Activity ----
    [ObservableProperty]
    private ObservableCollection<OperationJob> _activityJobs = [];

    /// <summary>活动列表为空时显示空状态文案，空表格会被误读为加载失败（真实账户下常见）。</summary>
    public bool HasActivity => ActivityJobs.Count > 0;

    public string ActivityEmptyText => _scopeContext.ActiveAccount is null
        ? "暂无活动记录。平台上的写操作执行后会显示在这里。"
        : "当前账户在此虚拟机上还没有操作记录。执行启动 / 重启 / 快照 / 端口变更后会显示在这里。";

    partial void OnActivityJobsChanged(ObservableCollection<OperationJob> value)
        => OnPropertyChanged(nameof(HasActivity));

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
        IVmDetailService detailService,
        IVmDiskService diskService,
        IVmMetricsService metrics,
        ICurrentIpProvider currentIp,
        IJobStore jobStore,
        IOperationEngine engine,
        OperationRequestFactory requests,
        IApprovalPolicy approvalPolicy,
        IShellNavigation navigation,
        ScopeContext scopeContext,
        SshConnectionService ssh,
        SshConnectFlow connectFlow)
    {
        _approvalPolicy = approvalPolicy;
        _power = power;
        _network = network;
        _detailService = detailService;
        _diskService = diskService;
        _metrics = metrics;
        _currentIp = currentIp;
        _jobStore = jobStore;
        _engine = engine;
        _requests = requests;
        _navigation = navigation;
        _scopeContext = scopeContext;
        _ssh = ssh;
        _connectFlow = connectFlow;
        _engine.JobUpdated += OnEngineJobUpdated;
    }

    /// <summary>当前 VM（Header 按钮的可见性随 PowerState 联动）。</summary>
    public VmSummary Vm => _vm;

    public void Initialize(VmSummary vm)
    {
        // 这里刻意不碰任何终端会话：会话归 TerminalPanelViewModel（应用级单例）所有，
        // 换一台 VM 或离开详情页都只是换个页面，已建立的连接继续活在底部面板里。

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

        LoadAll();
    }

    /// <summary>
    /// 重新读取这台虚拟机的全部详情。与 <see cref="Initialize"/> 共用 <see cref="LoadAll"/> ——
    /// 刷新若是第二套加载顺序，两边迟早会漂移，且漂移只在"点刷新才出现"的路径上暴露。
    /// </summary>
    [RelayCommand]
    private void Refresh() => LoadAll();

    /// <summary>
    /// 四个读取各自独立、各自容错，故意不 await：一个失败不该拖住另外三个，
    /// 每个 Load 内部已经各自把失败写进提示条。
    /// </summary>
    private void LoadAll()
    {
        _ = LoadOverviewAsync();
        _ = LoadNetworkAsync();
        _ = LoadDisksAsync();
        _ = LoadMetricsAsync();
        RefreshActivity();
    }

    /// <summary>
    /// 概览页：先用手上就有清单数据渲染一版（切页立刻有内容），再读 ARM 详情补齐规格能力 /
    /// 安全性 / 休眠 / 创建时间等清单里没有的字段。详情读失败**不**清空已有内容，
    /// 只把失败写进提示条 —— 概览的空缺不等于"这台机器什么都没有"。
    /// </summary>
    private async Task LoadOverviewAsync()
    {
        OverviewSections = BuildOverviewSections(null);

        try
        {
            var detail = await _detailService.GetAsync(_vm.ResourceId);
            if (detail is null)
            {
                // ARM 里已经没有这台 VM（详情页还开着是因为列表是上一次查询的结果）
                InfoSeverity = "Warning";
                InfoText = "该虚拟机在 Azure 中已不存在，详情信息无法读取。";
                return;
            }

            OverviewSections = BuildOverviewSections(detail);
        }
        catch (Exception ex)
        {
            InfoSeverity = "Failed";
            InfoText = $"虚拟机详情读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 合成概览分组。<paramref name="detail"/> 为 null 时只渲染清单侧就有的字段。
    ///
    /// 每一行都先判空再入列 —— 这是"Azure 没返回的字段不显示"的唯一实现点。
    /// 唯一的例外是「完整性监视」：ARM 与 Compute SDK 都不返回该字段（实测确认），
    /// 如果按同样规则丢掉，用户就永远看不到这一项，反而会以为漏做了，
    /// 所以这一行**始终显示**并标注原因。
    /// </summary>
    private IReadOnlyList<OverviewSection> BuildOverviewSections(VmDetailInfo? detail)
    {
        var sections = new List<OverviewSection>();

        sections.Add(Section("上下文",
            Row("订阅", _vm.SubscriptionName),
            Row("订阅 ID", _vm.SubscriptionId, copy: true),
            Row("资源组", _vm.ResourceGroupName),
            Row("区域", _vm.Region)));

        sections.Add(Section("大小",
            Row("大小", detail?.VmSize ?? _vm.VmSize),
            Row("vCPU", detail?.VCpusText),
            Row("RAM", detail?.MemoryText),
            Row("每个核心的线程数", detail?.VCpusPerCoreText)));

        sections.Add(Section("操作系统",
            Row("操作系统", OsTypeText),
            Row("计算机名", _vm.ComputerName),
            Row("管理员用户名", detail?.AdminUsername)));

        sections.Add(Section("安全性",
            Row("安全类型", detail?.SecurityTypeText),
            Row("启用安全启动", detail?.SecureBootText),
            Row("启用 vTPM", detail?.VTpmText),
            // 始终显示：读不到就是读不到，如实说明，不留空白让人猜
            new OverviewRow
            {
                Label = "完整性监视",
                Value = detail?.IntegrityMonitoringText ?? "—",
                ToolTip = "Azure ARM 接口未返回该虚拟机的完整性监视状态，因此无法判断是否启用。"
            }));

        sections.Add(Section("电源与计划",
            Row("电源状态", _vm.StatusText),
            Row("ProvisioningState", detail?.ProvisioningState),
            Row("休眠", detail?.HibernationText),
            Row("自动关闭", detail?.AutoShutdownText),
            Row("已计划的关闭", detail?.ScheduledShutdownText)));

        sections.Add(Section("位置与可用性",
            Row("创建时间", detail?.TimeCreatedText),
            Row("可用性区域", detail?.ZonesText),
            Row("可用性集", detail?.AvailabilitySetName),
            Row("VM ID", detail?.VmId, copy: true)));

        // 空分组整组不显示：只要有一行没值就算了，避免出现只有标题的空壳卡片
        return [.. sections.Where(s => s.Rows.Count > 0)];
    }

    private static OverviewSection Section(string title, params OverviewRow?[] rows) => new()
    {
        Title = title,
        Rows = [.. rows.Where(r => r is not null).Select(r => r!)]
    };

    /// <summary>构造一行；值为空则返回 null，由 <see cref="Section"/> 过滤掉，该行不显示。</summary>
    private static OverviewRow? Row(string label, string? value, bool copy = false) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new OverviewRow { Label = label, Value = value, CopyValue = copy ? value : null };

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
                SubnetText = string.IsNullOrEmpty(ctx.SubnetCidr)
                    ? ctx.SubnetName
                    : $"{ctx.SubnetName} ({ctx.SubnetCidr})";
                NsgName = ctx.NsgName;
                IsSharedSubnetNsg = ctx.IsSharedSubnetNsg;
                InboundRules = [.. ctx.InboundRules.OrderBy(r => r.Priority)];
                OutboundRules = [.. ctx.OutboundRules.OrderBy(r => r.Priority)];

                // 状态与文案都必须来自读到的数据：网卡与子网都没有 NSG 是一个真实的安全发现，
                // 不能笼统地写"NSG 规则已生效"。
                var hasNsg = !string.IsNullOrEmpty(ctx.NicNsgId) || !string.IsNullOrEmpty(ctx.SubnetNsgId);
                SecurityState = hasNsg ? "Protected" : "Unprotected";
                NsgStateText = hasNsg
                    ? $"NSG 规则已生效（{ctx.InboundRules.Count} 条入站 / {ctx.OutboundRules.Count} 条出站）"
                    : "网卡与子网均未关联网络安全组，流量不受 NSG 约束";

                // 公网 / 专用 IP 以 NIC 为权威来源：
                // Resource Graph 的 VM 视图不含 privateIps，早期实现会同时丢失专用 IP
                PublicIp = ctx.PublicIp ?? PublicIp;
                PublicIpName = ctx.PublicIpName;
                // DNS 名称跟着公网 IP 走：换了 IP 就必须换 DNS 行，不能把上一台的残留留着
                PublicIpFqdn = ctx.PublicIpFqdn;
                PrivateIp = ctx.PrivateIp ?? PrivateIp;

                ApplyOutbound(ctx.Outbound);
            }
            else
            {
                // 没有网卡 = 读不到网络上下文。既不能留白让人以为"没有网络"，
                // 更不能保留上一次的状态与"受保护"结论。
                InboundRules = [];
                OutboundRules = [];
                ApplyOutbound(null);
                PublicIpFqdn = null;
                NicName = "";
                VnetName = "";
                SubnetText = "";
                NsgName = "";
                IsSharedSubnetNsg = false;
                SecurityState = "Unknown";
                NsgStateText = "未读到网络配置（该虚拟机可能未关联网卡）";
            }
        }
        catch (Exception ex)
        {
            // 读取失败必须可见：空白的网络页会被误读为“这台 VM 没有网络配置”
            InboundRules = [];
            OutboundRules = [];
            ApplyOutbound(null);
            // 读失败时不能留着上一次的 DNS 名称：那会把上一台 VM 的域名安到这一台上
            PublicIpFqdn = null;
            SecurityState = "Unknown";
            NsgStateText = "网络信息读取失败";
            InfoSeverity = "Failed";
            InfoText = $"网络信息读取失败：{ex.Message}";
        }
        finally
        {
            IsLoadingNetwork = false;
        }
    }

    /// <summary>
    /// 把解析结果投影到界面。null 表示**未解析**（没有网卡，或读取整体失败），
    /// 此时卡片留空并说明原因 —— 绝不能借用 <see cref="OutboundConnectivityType.None"/>
    /// 的文案"无公网出口"，那是一个需要证据才能下的结论。
    /// </summary>
    private void ApplyOutbound(VmOutboundOverview? outbound)
    {
        if (outbound is null)
        {
            OutboundTypeText = "";
            OutboundBadge = "Unknown";
            OutboundIpText = "";
            OutboundAddress = null;
            OutboundExplanation = "";
            OutboundNicRows = [];
            OutboundSuppressedText = "";
            return;
        }

        var effective = outbound.Effective;
        OutboundTypeText = OutboundText.TypeLabel(effective.Type);
        OutboundBadge = OutboundText.BadgeOf(effective);
        OutboundIpText = OutboundText.DescribeIp(effective);
        OutboundAddress = effective.OutboundIp;
        OutboundExplanation = effective.Explanation;
        OutboundSuppressedText = OutboundText.DescribeSuppressed(outbound.Candidates);

        OutboundNicRows =
        [
            .. outbound.Nics.Select(nic => new OutboundNicRow
            {
                NicName = string.IsNullOrWhiteSpace(nic.NicName) ? "（未命名网卡）" : nic.NicName,
                IsPrimary = nic.IsPrimary,
                TypeLabel = OutboundText.TypeLabel(nic.Result.Type),
                Badge = OutboundText.BadgeOf(nic.Result),
                IpText = OutboundText.DescribeIp(nic.Result),
                Explanation = nic.Result.Explanation,
                SubnetText = nic.SubnetName ?? "",
                RoutedToVirtualAppliance = nic.RoutedToVirtualAppliance,
            })
        ];
    }

    private void RefreshActivity()
    {
        // 先按当前账户过滤：别的账户（含 Demo）在同一 ResourceId 上的记录不能出现在这里
        ActivityJobs = [.. ActiveAccountJobs.For(_scopeContext, _jobStore.GetAll())
            .Where(j => string.Equals(j.ResourceId, _vm.ResourceId, StringComparison.OrdinalIgnoreCase))
            .Take(20)];
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivityEmptyText));
    }

    private async Task LoadDisksAsync()
    {
        try
        {
            var disks = await _diskService.GetDisksAsync(_vm.ResourceId);
            Disks = [.. disks];
            SnapshotTotal = disks.Sum(d => d.SnapshotCount);
        }
        catch (Exception ex)
        {
            // 读取失败必须可见：空白的磁盘页会被误读为"这台 VM 没有磁盘"
            Disks = [];
            SnapshotTotal = 0;
            InfoSeverity = "Failed";
            InfoText = $"磁盘信息读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 读取 Azure Monitor 主机指标（设计文档 §27）。
    /// 指标不可用时保留 "—"，不填充 0 —— 0% CPU 和"没有数据"是两件事。
    /// </summary>
    private async Task LoadMetricsAsync()
    {
        try
        {
            var metrics = await _metrics.GetForVmAsync(_vm.ResourceId);

            CpuText = FormatPercent(metrics.CpuPercent);
            MemoryText = FormatPercent(metrics.MemoryPercent);
            OsDiskIopsText = FormatPercent(metrics.OsDiskIopsPercent);
            OsDiskBandwidthText = FormatPercent(metrics.OsDiskBandwidthPercent);
            VmCachedIopsText = FormatPercent(metrics.VmCachedIopsPercent);
            VmCachedBandwidthText = FormatPercent(metrics.VmCachedBandwidthPercent);
            VmUncachedIopsText = FormatPercent(metrics.VmUncachedIopsPercent);
            VmUncachedBandwidthText = FormatPercent(metrics.VmUncachedBandwidthPercent);
            NetworkInText = FormatRate(metrics.NetworkInBytesPerSecond);
            NetworkOutText = FormatRate(metrics.NetworkOutBytesPerSecond);
            DiskReadText = FormatRate(metrics.DiskReadBytesPerSecond);
            DiskWriteText = FormatRate(metrics.DiskWriteBytesPerSecond);

            MetricsNote = _scopeContext.ActiveAccount is null
                ? "演示数据（未登录 Azure）。登录后将显示 Azure Monitor 主机指标。"
                : metrics.HasAnyValue
                    ? $"Azure Monitor 主机指标，聚合窗口 {(int)metrics.Interval.TotalMinutes} 分钟；最后一个数据点 " +
                      $"{metrics.SampledAt?.ToLocalTime():HH:mm:ss}（指标有分钟级延迟）。"
                    : "Azure Monitor 未返回数据点 —— 虚拟机可能已解除分配或平台尚未产出指标。";
        }
        catch (Exception ex)
        {
            CpuText = MemoryText = OsDiskIopsText = OsDiskBandwidthText = "—";
            VmCachedIopsText = VmCachedBandwidthText = VmUncachedIopsText = VmUncachedBandwidthText = "—";
            NetworkInText = NetworkOutText = DiskReadText = DiskWriteText = "—";
            MetricsNote = $"性能指标读取失败：{ex.Message}";
        }
    }

    private static string FormatPercent(double? value) =>
        value is null ? "—" : $"{value:0.#}%";

    /// <summary>字节/秒 → 便于阅读的速率单位。</summary>
    private static string FormatRate(double? bytesPerSecond) => bytesPerSecond switch
    {
        null => "—",
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.#} KB/s",
        _ => $"{bytesPerSecond / (1024 * 1024):0.##} MB/s"
    };

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
        var confirmed = Views.ConfirmDialog.Show(
            "关机",
            "虚拟机将停止，但计算资源仍保留分配。费用可能继续产生。",
            "关机", isDanger: true);
        if (!confirmed)
        {
            return;
        }
        var job = await _power.PowerOffAsync(_vm);
        ShowJob(job);
    }

    [RelayCommand]
    private async Task DeallocateAsync()
    {
        var confirmed = Views.ConfirmDialog.Show(
            "解除分配",
            "虚拟机将停止并释放计算资源，停止计算计费（磁盘与保留 IP 可能继续计费）。",
            "停止并解除分配", isDanger: true);
        if (!confirmed)
        {
            return;
        }
        var job = await _power.DeallocateAsync(_vm);
        ShowJob(job);
    }

    // ==== Port Manager（§21–§24）====

    /// <summary>
    /// 打开端口（§23）。参数是规则方向：入站与出站各有一个按钮，用 CommandParameter 区分。
    /// 出站规则与入站规则在 NSG 里是同一种资源，只有 Direction 与"用户填的地址放哪一侧"不同，
    /// 所以共用这一条命令而不是两条几乎一样的。
    /// </summary>
    [RelayCommand]
    private async Task OpenPortAsync(string? directionText)
    {
        var direction = string.Equals(
            directionText, nameof(Modules.Network.Models.NsgRuleDirection.Outbound), StringComparison.OrdinalIgnoreCase)
            ? Modules.Network.Models.NsgRuleDirection.Outbound
            : Modules.Network.Models.NsgRuleDirection.Inbound;

        // 真实账户下这是本机出口公网 IP（HTTPS 回显查到）；查不到时为 null，
        // 对话框会要求手填来源而不是把演示地址当成用户自己的 IP。
        var ip = await _currentIp.GetCurrentIpAsync();
        // CIDR 从同一个 IP 推导。这里不再单独调一次 GetCurrentIpCidrAsync：
        // 两次独立查询可能拿到不同地址，且失败时会连着打两轮第三方端点。
        var cidr = Modules.Network.Services.PublicIpText.ToCidr(ip);
        var isDemoValue = _scopeContext.ActiveAccount is null;
        var dialog = new Views.OpenPortDialog(ip, cidr, isDemoValue, _networkContext?.SubnetCidr, direction)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() is not true || dialog.Result is null)
        {
            return;
        }

        var result = dialog.Result;
        var isOutbound = direction == Modules.Network.Models.NsgRuleDirection.Outbound;

        // 目标 NSG 必须由这里指名：读路径聚合了 NIC / Subnet 两个 NSG，
        // 只有网络上下文知道往哪一个里加规则（过去没有这一步，实现只好编造规则 ID）。
        var isSubnet = string.Equals(result.Origin, nameof(Modules.Network.Models.NsgRuleOrigin.Subnet),
            StringComparison.OrdinalIgnoreCase);
        var nsgId = isSubnet ? _networkContext?.SubnetNsgId : _networkContext?.NicNsgId;
        if (string.IsNullOrEmpty(nsgId))
        {
            // 该作用域下没有对应 NSG：如实告知，而不是让操作走到 Azure 再失败
            InfoSeverity = "Warning";
            InfoText = isSubnet
                ? "该虚拟机所在子网没有关联网络安全组，无法在其中新增子网级规则。"
                : "该虚拟机的网卡没有关联网络安全组，无法在其中新增网卡级规则。";
            return;
        }

        // 对端为"任意"时交给 Operation Engine 审批流（§25 原则）。
        // 出站的 "Internet" 也算：那是一条显式放行，虽然没超出 Azure 默认出站的范围，
        // 但它会压过后加的拒绝规则，值得让用户看一眼。
        var isBroadPeer = result.PeerPrefix is "*" or "0.0.0.0/0" or "Internet";
        var risk = isBroadPeer ? RiskLevel.High : RiskLevel.Medium;
        // 广对端一律不预先批准（这跟策略档位无关，是这条规则本身的性质）；
        // 其余按策略档位决定。子网级 NSG 另有引擎级的 §25 兜底，见 ImpactAssessment.CannotBypass。
        var preApproved = !isBroadPeer && _approvalPolicy.ShouldAutoApprove(risk);

        // 用户填的对端按方向落到对应的 payload 键：入站是来源，出站是目标。
        // 两个键都写全，Handler 按 direction 取用 —— 不靠它去猜哪一侧是用户填的。
        var job = await _engine.SubmitAsync(_requests.Create(
            "network.open_port",
            _vm.SubscriptionId,
            _vm.ResourceId,
            isOutbound ? $"开放出站端口 {result.Port}" : $"打开端口 {result.Port}",
            risk: risk,
            preApproved: preApproved,
            payload: new Dictionary<string, string>
            {
                ["nsgId"] = nsgId,
                ["ruleName"] = result.RuleName,
                ["port"] = result.Port.ToString(),
                ["protocol"] = result.Protocol,
                ["direction"] = result.Direction,
                ["origin"] = result.Origin,
                ["priority"] = result.Priority.ToString(),
                ["sourcePrefix"] = isOutbound ? "*" : result.PeerPrefix,
                ["sourceDisplay"] = isOutbound ? "Any" : result.PeerDisplay,
                ["destinationPrefix"] = isOutbound ? result.PeerPrefix : "*",
                ["destinationDisplay"] = isOutbound ? result.PeerDisplay : "Any"
            }));

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

        // 这里的 preApproved 只代表"风险类审批"这一层：对话框已经让用户确认过端口。
        // 子网级 NSG 的 §25 影响面确认不归它管 —— 引擎按 CannotBypass 强制弹出，
        // 无论这里传什么、也无论设置里选了哪一档。
        var job = await _engine.SubmitAsync(_requests.Create(
            "network.change_port",
            _vm.SubscriptionId,
            _vm.ResourceId,
            $"更改端口 {rule.Name}：{rule.DestinationPort} → {newPort}",
            risk: RiskLevel.Medium,
            preApproved: _approvalPolicy.ShouldAutoApprove(RiskLevel.Medium),
            payload: new Dictionary<string, string>
            {
                ["ruleId"] = rule.RuleId,
                ["ruleName"] = rule.Name,
                ["port"] = newPort.ToString()
            }));

        await AfterRuleJobAsync(job);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(NsgSecurityRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        var confirmed = Views.ConfirmDialog.Show(
            "删除规则",
            $"确定要删除规则“{rule.Name}”（端口 {rule.DestinationPort}）吗？该操作会先经过影响分析。",
            "删除", isDanger: true);
        if (!confirmed)
        {
            return;
        }

        var job = await _engine.SubmitAsync(_requests.Create(
            "network.delete_rule",
            _vm.SubscriptionId,
            _vm.ResourceId,
            $"删除规则 {rule.Name}",
            risk: RiskLevel.Medium,
            // 同 ChangePort：这里只是 MessageBox 确认过，§25 的共享子网确认由引擎另做
            preApproved: _approvalPolicy.ShouldAutoApprove(RiskLevel.Medium),
            payload: new Dictionary<string, string>
            {
                ["ruleId"] = rule.RuleId,
                ["ruleName"] = rule.Name
            }));

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
        // §25：先把影响面摆给用户，由用户决定继续还是取消 —— 不能只给一个"批准"按钮。
        var dialog = new Views.ImpactApprovalDialog(PendingApprovalJob)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        var job = await _engine.ApproveAsync(PendingApprovalJob.JobId);
        PendingApprovalJob = null;
        await AfterRuleJobAsync(job);
    }

    /// <summary>
    /// 作废当前挂起审批。批准的对偶出口：不想继续的挂起 Job 不能只能「批准」或永远挂着。
    /// </summary>
    [RelayCommand]
    private async Task RejectAsync()
    {
        if (PendingApprovalJob is null)
        {
            return;
        }

        var confirmed = Views.ConfirmDialog.Show(
            "作废任务",
            $"将作废任务「{PendingApprovalJob.Display}」。作废后该任务不再执行，状态变为已取消，并写入审计日志。此操作不可撤销。",
            "作废", isDanger: true);

        if (!confirmed)
        {
            return;
        }

        var job = await _engine.RejectAsync(PendingApprovalJob.JobId, "详情页作废");
        PendingApprovalJob = null;
        ShowJob(job);
        RefreshActivity();
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
            JobStatus.Succeeded => $"{job.Display} —— 成功（已验证）。",
            JobStatus.Failed => $"{job.Display} —— 失败：{job.Error}",
            JobStatus.WaitingApproval => $"{job.Display} —— 等待审批。",
            _ => $"{job.Display} —— {CfStatusTextConverter.Map(job.Status.ToString())}…"
        };

        // 终态后联动刷新活动与磁盘快照计数
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed)
        {
            RefreshActivity();
            _ = LoadDisksAsync();
        }
    }

    /// <summary>启动（Stopped / Deallocated → Running）。</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        var job = await _power.StartAsync(_vm);
        ShowJob(job);
    }

    /// <summary>创建快照（磁盘级，disk.snapshot 经 Operation Engine，P1 Exit Gate 项）。</summary>
    [RelayCommand]
    private async Task CreateSnapshotAsync(VmDiskInfo? disk)
    {
        if (disk is null)
        {
            return;
        }

        var job = await _engine.SubmitAsync(_requests.Create(
            "disk.snapshot",
            _vm.SubscriptionId,
            _vm.ResourceId,
            $"创建快照 {disk.Name}",
            risk: RiskLevel.Low,
            // 快照不改动现有资源，但它是写操作 —— 策略选了「所有写操作」时照样要停下来
            preApproved: _approvalPolicy.ShouldAutoApprove(RiskLevel.Low),
            payload: new Dictionary<string, string>
            {
                ["diskId"] = disk.DiskId,
                ["diskName"] = disk.Name,
                // 快照名在提交时固化：执行时现编一个，Verify 就没有稳定的名字去找它
                ["snapshotName"] = $"{disk.Name}-snapshot-{DateTimeOffset.Now:yyyyMMddHHmmss}"
            }));

        ShowJob(job);
    }

    /// <summary>更改规格（code-behind 菜单入口打开对话框并提交 vm.resize）。</summary>
    public async Task ResizeFromMenuAsync(string newSize)
    {
        var job = await _power.ResizeAsync(_vm, newSize);
        if (job.Status == JobStatus.Succeeded)
        {
            VmSize = _vm.VmSize; // Header 元信息联动
        }
        ShowJob(job);
    }

    public IReadOnlyList<string> ResizeSizeOptions => ResizeVmHandler.SupportedSizes;

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

            // 停在审批的 Job 统一在这里挂起：电源/规格/网络三条提交路径都会经过事件，
            // 不必在每一条命令里各写一遍，也就不会漏掉某一条（重启曾因此卡在"等待审批"无出口）。
            if (job.Status == JobStatus.WaitingApproval)
            {
                PendingApprovalJob = job;
            }
        });
    }

    // ==== 通用 ====

    /// <summary>
    /// Connect（设计文档 §28）：Windows → mstsc；Linux → 主窗口底部终端面板里的 SSH 标签
    /// （凭据对话框 → 会话交给面板，参考 RemoteFlow 连接逻辑，见实现标准文档）。
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        var target = PublicIp ?? PrivateIp;
        if (string.IsNullOrEmpty(target))
        {
            System.Windows.MessageBox.Show("该虚拟机没有可用的 IP 地址（未分配）。",
                "连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_vm.OsType == VmOsType.Windows)
            {
                System.Diagnostics.Process.Start("mstsc", $"/v:{target}");
                return;
            }

            await ConnectLinuxAsync(target);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动连接失败：{ex.Message}", "连接",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Linux VM：凭据对话框 → 让 <see cref="SshConnectFlow"/> 在底部终端面板里建立会话。
    /// 同一台 VM 再次连接会复用它的标签并替换会话；换页面不影响已建立的会话。
    /// </summary>
    /// <remarks>
    /// 这里只留"问用户用哪条凭据"与"把失败告诉用户"两件事；其余（落库、解析凭据、
    /// 记录最近使用、开会话）全在 <see cref="SshConnectFlow"/> 里 —— 列表页的连接走同一份实现。
    /// </remarks>
    private async Task ConnectLinuxAsync(string host)
    {
        var credentialDialog = new Views.SshCredentialDialog(
            _connectFlow.CredentialLibrary, host, _vm.ResourceId)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (credentialDialog.ShowDialog() is not true || credentialDialog.Result is null)
        {
            return;
        }

        var input = credentialDialog.Result;

        var request = new SshConnectRequest
        {
            CredentialId = input.UseCredentialId,
            TransientInput = input,
        };

        var result = await _connectFlow.ConnectAsync(
            _vm, host, request, _connectFlow.CreateInteractivePolicy());

        if (!result.Connected)
        {
            MessageBox.Show(result.FailureMessage, "连接",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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

    /// <summary>
    /// 供 code-behind 菜单使用：Deallocate。返回 <see cref="Task"/> 而不是 <c>async void</c>——
    /// 调用方（真正的 WPF 事件处理器）才是异常兜底的正确落点，这里只做转发。
    /// </summary>
    public Task DeallocateFromMenuAsync() => DeallocateCommand.ExecuteAsync(null);
}
