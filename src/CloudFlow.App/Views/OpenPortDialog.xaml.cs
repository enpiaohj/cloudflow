using System.ComponentModel;
using System.Windows;

namespace CloudFlow.App.Views;

/// <summary>Open Port 结果（提交 Operation Engine 的参数，设计文档 §23/§24）。</summary>
public sealed record OpenPortResult(
    string RuleName,
    int Port,
    string Protocol,
    string SourcePrefix,
    string SourceDisplay,
    string Origin,
    int Priority);

/// <summary>
/// Open Port 对话框（概念图 2 右侧面板）。
/// Source 默认 My Current IP（§24 My Current IP 一键开放）。
/// </summary>
public partial class OpenPortDialog : Window, INotifyPropertyChanged
{
    private string _ruleName = "AppAccess";
    private string _portText = "";
    private string _selectedProtocol = "TCP";
    private string? _selectedSource;
    private string _priorityText = "400";
    private string _selectedApplyTo = "Network Interface (NIC)";
    private string _errorText = "";

    private readonly string _myIpDisplay;
    private readonly string _myIpCidr;

    public string[] ProtocolOptions { get; } = ["TCP", "UDP"];

    public string[] SourceOptions { get; }

    public string[] ApplyToOptions { get; } = ["Network Interface (NIC)", "Subnet NSG (shared)"];

    public OpenPortResult? Result { get; private set; }

    public OpenPortDialog(string currentIp, string currentIpCidr)
    {
        _myIpDisplay = $"My Current IP ({currentIp})";
        _myIpCidr = currentIpCidr;
        SourceOptions = [_myIpDisplay, "Any", "10.0.2.0/24"];
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
            ErrorText = "Rule name is required.";
            return;
        }

        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorText = "Port must be an integer between 1 and 65535.";
            return;
        }

        if (!int.TryParse(PriorityText.Trim(), out var priority) || priority is < 100 or > 4096)
        {
            ErrorText = "Priority must be between 100 and 4096.";
            return;
        }

        var source = (SelectedSource ?? "").Trim();
        if (source.Length == 0)
        {
            ErrorText = "Source is required.";
            return;
        }

        string prefix;
        string display;
        if (source == _myIpDisplay)
        {
            prefix = _myIpCidr;                    // 例如 203.0.113.10/32
            display = $"My IP ({_myIpCidr[..^3]})";
        }
        else if (source == "Any")
        {
            prefix = "*";
            display = "Any";
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
                ErrorText = "Source must be 'Any', 'My Current IP' or a valid IP/CIDR.";
                return;
            }
            prefix = source;
            display = source;
        }

        var origin = SelectedApplyTo.StartsWith("Subnet") ? "Subnet" : "Nic";

        Result = new OpenPortResult(name, port, SelectedProtocol, prefix, display, origin, priority);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
