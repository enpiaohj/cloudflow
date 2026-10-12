using System.ComponentModel;
using System.Windows;
using CloudFlow.App.Themes;

namespace CloudFlow.App.Views;

/// <summary>
/// 更改规格对话框（设计文档 §19 Resize；vm.resize 经 Operation Engine 提交）。
/// </summary>
public partial class ResizeDialog : CfDialogWindow, INotifyPropertyChanged
{
    private string? _selectedSize;
    private string _errorText = "";

    public string CurrentSize { get; }

    public string[] SizeOptions { get; }

    public string? SelectedSize
    {
        get => _selectedSize;
        set
        {
            _selectedSize = value;
            ErrorText = "";
            OnPropertyChanged(nameof(SelectedSize));
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

    /// <summary>确认后的目标规格（DialogResult = true 时有效）。</summary>
    public string NewSize { get; private set; } = "";

    public ResizeDialog(string currentSize, IReadOnlyList<string> supportedSizes)
    {
        CurrentSize = currentSize;
        SizeOptions = [.. supportedSizes.Where(s => !string.Equals(s, currentSize, StringComparison.OrdinalIgnoreCase))];
        _selectedSize = SizeOptions.FirstOrDefault();
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedSize))
        {
            ErrorText = "请选择目标规格。";
            return;
        }
        NewSize = SelectedSize;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
