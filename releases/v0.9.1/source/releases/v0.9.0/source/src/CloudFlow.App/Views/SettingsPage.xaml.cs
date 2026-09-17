using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace CloudFlow.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 「关于」页的仓库链接。WPF 的 Hyperlink 在桌面应用里不会自己开浏览器，
    /// 必须显式交给系统 Shell；UseShellExecute=true 走默认浏览器。
    /// </summary>
    private void OpenRepositoryLink_Click(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.AbsoluteUri is { Length: > 0 } url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        e.Handled = true;
    }
}
