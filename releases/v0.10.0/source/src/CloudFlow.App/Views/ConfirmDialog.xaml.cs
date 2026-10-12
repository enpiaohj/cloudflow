using System.Windows;
using CloudFlow.App.Themes;

namespace CloudFlow.App.Views;

/// <summary>
/// 统一的二次确认对话框，取代散落在各 ViewModel 里的 <see cref="MessageBox"/> 调用——
/// 原生系统对话框的 OK/Cancel/Yes/No 按钮与应用其余全中文自定义对话框观感不一致，
/// 且不跟随 CloudFlow 自己的主题。破坏性或需要用户明确决定的操作统一走这里。
/// </summary>
public partial class ConfirmDialog : CfDialogWindow
{
    public string DialogTitle { get; }

    public string Message { get; }

    public string ConfirmButtonText { get; }

    /// <summary>确认按钮是否按危险操作着色（红色）。</summary>
    public bool IsDanger { get; }

    public bool IsPrimary => !IsDanger;

    public ConfirmDialog(string title, string message, string confirmButtonText, bool isDanger = false)
    {
        DialogTitle = title;
        Message = message;
        ConfirmButtonText = confirmButtonText;
        IsDanger = isDanger;

        InitializeComponent();
        DataContext = this;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 弹出确认对话框，返回用户是否确认。<paramref name="owner"/> 为 <c>null</c> 时
    /// 回退到主窗口，与其余对话框的既有约定一致。
    /// </summary>
    public static bool Show(
        string title, string message, string confirmButtonText, bool isDanger = false, Window? owner = null)
    {
        var dialog = new ConfirmDialog(title, message, confirmButtonText, isDanger)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }
}
