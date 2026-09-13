using System.ComponentModel;
using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.App.Views;

/// <summary>
/// 打开端口结果（提交 Operation Engine 的参数，设计文档 §23/§24）。
///
/// <see cref="PeerPrefix"/> / <see cref="PeerDisplay"/> 是用户在对话框里填的那个对端地址：
/// <see cref="Direction"/> 为 Inbound 时它是来源，为 Outbound 时它是目标。
/// 用 "Peer" 而不是 "Source" 命名，是因为后者对出站规则是错的 —— 出站规则的来源是这台机器自己。
/// </summary>
public sealed record OpenPortResult(
    string RuleName,
    int Port,
    string Protocol,
    string PeerPrefix,
    string PeerDisplay,
    string Origin,
    string Direction,
    int Priority);

/// <summary>
/// 打开端口对话框（概念图 2 右侧面板）。入站与出站共用这一个窗体：
/// NSG 里两侧是同一种资源，只有方向与"用户填的那个地址放在哪一侧"不同，
/// 拆成两个窗体只会让两边的校验逻辑各自漂移。
/// </summary>
public partial class OpenPortDialog : CfDialogWindow, INotifyPropertyChanged
{
    private const string AnyInboundOption = "任意 (Any)";
    private const string AnyOutboundOption = "任意目标 (Any)";
    private const string InternetOption = "Internet";
    private const string VirtualNetworkOption = "虚拟网络 (VirtualNetwork)";

    private string _ruleName = "AppAccess";
    private string _portText = "";
    private string _selectedProtocol = "TCP";
    private string? _selectedPeer;
    private string _priorityText = "400";
    private string _selectedApplyTo = "网卡 NSG（仅本虚拟机）";
    private string _selectedDirection = DirectionOption(NsgRuleDirection.Inbound);
    private string _errorText = "";

    /// <summary>「我的当前 IP」选项文本；无法确定公网 IP 时为 null，该选项不出现。</summary>
    private readonly string? _myIpDisplay;

    /// <summary>与 <see cref="_myIpDisplay"/> 配对的前缀。只有入站方向会用到它。</summary>
    private readonly string? _myIpCidr;

    /// <summary>该 IP 是不是演示地址。提示行要据此说明来源，不能把演示值说成用户自己的出口地址。</summary>
    private readonly bool _myIpIsDemo;

    private readonly Dictionary<string, (string Prefix, string Display)> _knownPeers = new();

    private string[] _peerOptions = [];

    public string[] ProtocolOptions { get; } = ["TCP", "UDP"];

    /// <summary>
    /// 方向选项。文案里带上"谁访问谁"，因为 NSG 的 Inbound / Outbound 是相对虚拟机而言的，
    /// 只写"入站 / 出站"会让用户按自己所在的位置去理解，正好反过来。
    /// </summary>
    public string[] DirectionOptions { get; } =
    [
        DirectionOption(NsgRuleDirection.Inbound),
        DirectionOption(NsgRuleDirection.Outbound)
    ];

    public string[] ApplyToOptions { get; } = ["网卡 NSG（仅本虚拟机）", "子网 NSG（共享）"];

    public OpenPortResult? Result { get; private set; }

    /// <param name="currentIp">当前公网 IP；Demo 模式为演示地址，真实模式无法确定时为 null。</param>
    /// <param name="currentIpCidr">对应的 /32 CIDR；随 currentIp 一起为 null。</param>
    /// <param name="isDemoValue">true 表示 currentIp 是演示值，UI 必须标注，不能冒充用户真实 IP。</param>
    /// <param name="subnetCidr">该虚拟机所在子网 CIDR；未知时为 null。用它限制对端比硬编码示例网段有意义。</param>
    /// <param name="direction">初始方向。两个"新建端口规则"入口各自带上自己的方向。</param>
    public OpenPortDialog(
        string? currentIp,
        string? currentIpCidr,
        bool isDemoValue,
        string? subnetCidr,
        NsgRuleDirection direction = NsgRuleDirection.Inbound)
    {
        if (!string.IsNullOrWhiteSpace(currentIp) && !string.IsNullOrWhiteSpace(currentIpCidr))
        {
            _myIpDisplay = isDemoValue ? $"演示 IP ({currentIp})" : $"我的当前 IP ({currentIp})";
            _myIpCidr = currentIpCidr;
            _myIpIsDemo = isDemoValue;
        }

        SubnetCidr = subnetCidr;
        _selectedDirection = DirectionOption(direction);
        // 端口默认值按方向分开：8443 是"对外提供服务的端口"的思路，
        // 出站最常见的需求是访问外部的标准端口，给 443 比给 8443 更贴近实际
        _portText = direction == NsgRuleDirection.Outbound ? "443" : "8443";

        InitializeComponent();
        DataContext = this;

        RebuildPeers();

        // 默认选"仅自己 / 仅本虚拟网络"，但只有真的知道选什么时才敢默认；否则留空强制用户明确指定
        _selectedPeer = DefaultPeer();
        OnPropertyChanged(nameof(SelectedPeer));
        OnPropertyChanged(nameof(PeerHint));
    }

    private static string DirectionOption(NsgRuleDirection direction) =>
        direction == NsgRuleDirection.Outbound
            ? "出站（允许本机访问外部端口）"
            : "入站（允许外部访问本机端口）";

    /// <summary>该虚拟机所在子网 CIDR，构造时记下来供切换方向时重建选项。</summary>
    private string? SubnetCidr { get; }

    public NsgRuleDirection Direction =>
        SelectedDirection == DirectionOption(NsgRuleDirection.Outbound)
            ? NsgRuleDirection.Outbound
            : NsgRuleDirection.Inbound;

    /// <summary>
    /// 窗口标题栏与页内标题。与网络页那两张规则卡上的按钮同名 —— 用户是从那两个按钮进来的，
    /// 标题换个说法只会让人怀疑点错了地方。
    /// 出站不能也叫「打开端口」：出站规则不开本机的任何端口，它放行的是本机往外走的目标端口。
    /// </summary>
    public string DialogTitle => Direction == NsgRuleDirection.Outbound
        ? "新建出站规则"
        : "新建入站规则";

    /// <summary>副标题，随方向变化 —— 建出站规则时写"入站"是直接说反了。</summary>
    public string SubtitleText => Direction == NsgRuleDirection.Outbound
        ? "创建新的出站安全规则：允许这台虚拟机访问外部地址。"
        : "创建新的入站安全规则：允许外部访问这台虚拟机的端口。";

    /// <summary>对端那一行的标签。入站填来源、出站填目标，同一个输入框两种含义。</summary>
    public string PeerLabel => Direction == NsgRuleDirection.Outbound ? "目标" : "来源";

    /// <summary>对端下拉里的可选值，随方向重建。</summary>
    public string[] PeerOptions
    {
        get => _peerOptions;
        private set
        {
            _peerOptions = value;
            OnPropertyChanged(nameof(PeerOptions));
        }
    }

    /// <summary>端口那一行的说明，两侧语义不同。</summary>
    public string PortHint => Direction == NsgRuleDirection.Outbound
        ? "本机要访问的对端端口。"
        : "本机被访问的端口。";

    /// <summary>该动作的风险提示，随方向变化。</summary>
    public string WarningText => Direction == NsgRuleDirection.Outbound
        ? "注意：目标是“任意目标 (Any)”时将显式放行这台虚拟机的全部出站流量，该操作将进入审批流程。"
        : "注意：来源为“任意 (Any)”会将端口暴露给 Internet，该操作将进入审批流程。";

    /// <summary>对端下拉的辅助说明，提示为什么没有"我的当前 IP"选项。</summary>
    public string? PeerHint => Direction == NsgRuleDirection.Outbound
        ? null
        : _myIpDisplay is null
            ? "CloudFlow 无法自动确定你的公网 IP（查询失败，或已在设置中关闭「自动查询我的公网 IP」）。"
              + "请手动填写来源 IP/CIDR，例如 1.2.3.4/32。"
            : _myIpIsDemo
                ? "这是演示用的文档地址（RFC 5737），不是真实公网 IP；登录真实账户后会换成实际出口地址。"
                : "这个地址是从当前出口链路查到的，不是这台电脑的固定地址 —— 换出口或改代理后会变，"
                  + "届时这条规则放行的来源就失效了。";

    public string RuleName
    {
        get => _ruleName;
        set
        {
            _ruleName = value;
            OnPropertyChanged(nameof(RuleName));
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            _portText = value;
            ErrorText = "";
            OnPropertyChanged(nameof(PortText));
        }
    }

    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            _selectedProtocol = value;
            OnPropertyChanged(nameof(SelectedProtocol));
        }
    }

    public string? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            _selectedPeer = value;
            ErrorText = "";
            OnPropertyChanged(nameof(SelectedPeer));
        }
    }

    public string PriorityText
    {
        get => _priorityText;
        set
        {
            _priorityText = value;
            ErrorText = "";
            OnPropertyChanged(nameof(PriorityText));
        }
    }

    public string SelectedApplyTo
    {
        get => _selectedApplyTo;
        set
        {
            _selectedApplyTo = value;
            OnPropertyChanged(nameof(SelectedApplyTo));
        }
    }

    /// <summary>
    /// 切换方向。对端那一行的含义整个变了（来源 ↔ 目标），所以选项列表与已选值都要重建 ——
    /// 沿用上一个方向的来源当目标，会建出一条目标地址恰好是"我的 IP"的规则。
    /// </summary>
    public string SelectedDirection
    {
        get => _selectedDirection;
        set
        {
            if (string.Equals(_selectedDirection, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedDirection = value;
            OnPropertyChanged(nameof(SelectedDirection));
            OnPropertyChanged(nameof(Direction));
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(PeerLabel));
            OnPropertyChanged(nameof(PortHint));
            OnPropertyChanged(nameof(WarningText));
            OnPropertyChanged(nameof(PeerHint));

            RebuildPeers();
            _selectedPeer = DefaultPeer();
            OnPropertyChanged(nameof(SelectedPeer));
            ErrorText = "";
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            _errorText = value;
            OnPropertyChanged(nameof(ErrorText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string? DefaultPeer() => Direction == NsgRuleDirection.Outbound
        // 出站默认收在虚拟网络内：最常见的出站需求是访问同 VNet 里的数据库之类的服务，
        // 默认给 Internet 等于每次新建都顺手放开公网出口
        ? VirtualNetworkOption
        : _myIpDisplay;

    /// <summary>
    /// 按方向重建"已知对端"列表。只列真实已知的值：过去的 10.0.2.0/24 是 Demo 数据集里的
    /// 示例网段，在用户的真实订阅里既不是他的 IP 也不是他的子网，选中它只会建出一条没用的规则。
    /// </summary>
    private void RebuildPeers()
    {
        var options = new List<string>();
        _knownPeers.Clear();

        if (Direction == NsgRuleDirection.Outbound)
        {
            options.Add(VirtualNetworkOption);
            _knownPeers[VirtualNetworkOption] = ("VirtualNetwork", "VirtualNetwork");

            options.Add(InternetOption);
            _knownPeers[InternetOption] = ("Internet", "Internet");

            if (!string.IsNullOrWhiteSpace(SubnetCidr))
            {
                var subnetLabel = $"该虚拟机所在子网 ({SubnetCidr})";
                _knownPeers[subnetLabel] = (SubnetCidr, SubnetCidr);
                options.Add(subnetLabel);
            }

            options.Add(AnyOutboundOption);
        }
        else
        {
            if (_myIpDisplay is not null)
            {
                options.Add(_myIpDisplay);
                _knownPeers[_myIpDisplay] = (_myIpCidr!, _myIpDisplay);
            }

            if (!string.IsNullOrWhiteSpace(SubnetCidr))
            {
                var subnetLabel = $"该虚拟机所在子网 ({SubnetCidr})";
                _knownPeers[subnetLabel] = (SubnetCidr, SubnetCidr);
                options.Add(subnetLabel);
            }

            options.Add(AnyInboundOption);
        }

        PeerOptions = [.. options];
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = RuleName.Trim();
        if (name.Length == 0)
        {
            ErrorText = "请输入规则名称。";
            return;
        }

        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorText = "端口必须是 1–65535 之间的整数。";
            return;
        }

        if (!int.TryParse(PriorityText.Trim(), out var priority) || priority is < 100 or > 4096)
        {
            ErrorText = "优先级必须是 100–4096 之间的整数。";
            return;
        }

        var peer = (SelectedPeer ?? "").Trim();
        if (peer.Length == 0)
        {
            ErrorText = Direction == NsgRuleDirection.Outbound ? "请选择或输入目标。" : "请选择或输入来源。";
            return;
        }

        string prefix;
        string display;
        if (_knownPeers.TryGetValue(peer, out var known))
        {
            prefix = known.Prefix;
            display = known.Display;
        }
        else if (peer == AnyInboundOption)
        {
            prefix = "*";
            display = "任意 (Any)";
        }
        else if (peer == AnyOutboundOption)
        {
            prefix = "*";
            display = "任意目标 (Any)";
        }
        else
        {
            // 允许输入单个 IP 或 CIDR
            var normalized = peer.Contains('/') ? peer : peer + "/32";
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    normalized, @"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$"))
            {
                ErrorText = Direction == NsgRuleDirection.Outbound
                    ? "目标必须是下拉中的选项、“任意目标 (Any)”，或有效的 IP/CIDR（例如 1.2.3.4/32）。"
                    : "来源必须是下拉中的选项、“任意 (Any)”，或有效的 IP/CIDR（例如 1.2.3.4/32）。";
                return;
            }
            prefix = normalized;
            display = normalized;
        }

        var origin = SelectedApplyTo.StartsWith("子网") ? "Subnet" : "Nic";

        Result = new OpenPortResult(
            name, port, SelectedProtocol, prefix, display, origin, Direction.ToString(), priority);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
