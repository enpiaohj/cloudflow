using System.ComponentModel;
using System.Windows;

namespace CloudFlow.App.Views;

/// <summary>打开端口结果（提交 Operation Engine 的参数，设计文档 §23/§24）。</summary>
public sealed record OpenPortResult(
    string RuleName,
    int Port,
    string Protocol,
    string SourcePrefix,
    string SourceDisplay,
    string Origin,
    int Priority);

/// <summary>
/// 打开端口对话框（概念图 2 右侧面板）。
/// 来源默认我的当前 IP（§24 My Current IP 一键开放）。
/// </summary>
public partial class OpenPortDialog : Window, INotifyPropertyChanged
{
    private const string AnyOption = "任意 (Any)";

    private string _ruleName = "AppAccess";
    private string _portText = "";
    private string _selectedProtocol = "TCP";
    private string? _selectedSource;
    private string _priorityText = "400";
    private string _selectedApplyTo = "网卡 NSG（仅本虚拟机）";
    private string _errorText = "";

    private readonly string _myIpDisplay;
    private readonly string _myIpCidr;

    public string[] ProtocolOptions { get; } = ["TCP", "UDP"];

    public string[] SourceOptions { get; }

    public string[] ApplyToOptions { get; } = ["网卡 NSG（仅本虚拟机）", "子网 NSG（共享）"];

    public OpenPortResult? Result { get; private set; }

    public OpenPortDialog(string currentIp, string currentIpCidr)
    {
        _myIpDisplay = $"我的当前 IP ({currentIp})";
        _myIpCidr = currentIpCidr;
        SourceOptions = [_myIpDisplay, AnyOption, "10.0.2.0/24"];
        _selectedSource = _myIpDisplay;
        _portText = "8443";
        InitializeComponent();
        DataContext = this;
    }

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

    public string? SelectedSource
    {
        get => _selectedSource;
        set
        {
            _selectedSource = value;
            ErrorText = "";
            OnPropertyChanged(nameof(SelectedSource));
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

        var source = (SelectedSource ?? "").Trim();
        if (source.Length == 0)
        {
            ErrorText = "请选择或输入来源。";
            return;
        }

        string prefix;
        string display;
        if (source == _myIpDisplay)
        {
            prefix = _myIpCidr;                    // 例如 203.0.113.10/32
            display = $"我的 IP ({_myIpCidr[..^3]})";
        }
        else if (source == AnyOption)
        {
            prefix = "*";
            display = "任意 (Any)";
        }
        else
        {
            // 允许输入单个 IP 或 CIDR
            if (!source.Contains('/'))
            {
                source += "/32";
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$"))
            {
                ErrorText = "来源必须是“我的当前 IP”、“任意 (Any)”或有效的 IP/CIDR。";
                return;
            }
            prefix = source;
            display = source;
        }

        var origin = SelectedApplyTo.StartsWith("子网") ? "Subnet" : "Nic";

        Result = new OpenPortResult(name, port, SelectedProtocol, prefix, display, origin, priority);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
