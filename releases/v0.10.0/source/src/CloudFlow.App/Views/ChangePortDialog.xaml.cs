using System.ComponentModel;
using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.App.Views;

/// <summary>
/// Change Port 对话框（概念图 2，设计文档 §22）：
/// 只改端口，Protocol / Source / 其他设置保持不变。
/// </summary>
public partial class ChangePortDialog : CfDialogWindow, INotifyPropertyChanged
{
    private string _newPortText = "";
    private string _errorText = "";

    public string RuleName { get; }

    public int CurrentPort { get; }

    public string Protocol { get; }

    public string Source { get; }

    public int Priority { get; }

    /// <summary>确认后的新端口（DialogResult = true 时有效）。</summary>
    public int NewPort { get; private set; }

    public string NewPortText
    {
        get => _newPortText;
        set
        {
            _newPortText = value;
            ErrorText = "";
            OnPropertyChanged(nameof(NewPortText));
            OnPropertyChanged(nameof(ErrorText));
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

    public ChangePortDialog(NsgSecurityRule rule)
    {
        RuleName = rule.Name;
        CurrentPort = rule.DestinationPort;
        NewPortText = rule.DestinationPort.ToString();
        Protocol = rule.Protocol.ToString();
        Source = rule.Source;
        Priority = rule.Priority;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => NewPortBox.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewPortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorText = "端口必须是 1–65535 之间的整数。";
            return;
        }
        if (port == CurrentPort)
        {
            ErrorText = "新端口与当前端口相同。";
            return;
        }

        NewPort = port;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
