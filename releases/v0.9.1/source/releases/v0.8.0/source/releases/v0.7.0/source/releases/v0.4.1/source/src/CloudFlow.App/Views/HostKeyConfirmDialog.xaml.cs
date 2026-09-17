using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Terminal.Ssh;

namespace CloudFlow.App.Views;

/// <summary>
/// SSH 主机密钥首次信任 / 指纹变化确认框（RemoteFlow HostKeyPolicy 的 UI 侧）。
/// 返回 true = 信任并记录指纹；false = 拒绝，连接终态失败。
/// </summary>
public partial class HostKeyConfirmDialog : CfDialogWindow
{
    private readonly SshHostKey _key;

    public HostKeyConfirmDialog(SshHostKey key)
    {
        InitializeComponent();
        _key = key;

        HostText.Text = $"{key.Host}:{key.Port}";
        AlgorithmText.Text = key.KeyAlgorithm;
        FingerprintText.Text = key.Fingerprint;

        if (key.IsMismatch)
        {
            MismatchPanel.Visibility = Visibility.Visible;
            TrustButton.Content = "仍要信任并连接";
            IntroText.Text = "已记录的指纹与本机第一次连接时不同。";
        }
        else
        {
            IntroText.Text = "第一次连接这台主机，请核对指纹。";
        }
    }

    /// <summary>用户是否选择信任（ShowDialog() == true）。</summary>
    public bool Trusted => DialogResult is true;

    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
