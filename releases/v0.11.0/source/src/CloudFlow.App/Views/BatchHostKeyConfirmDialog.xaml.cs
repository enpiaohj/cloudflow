using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Terminal.Ssh;

namespace CloudFlow.App.Views;

/// <summary>
/// 批量连接时把"本次首次遇到的全部主机密钥"合并成<b>一次</b>确认。
/// </summary>
/// <remarks>
/// <para>
/// 单台连接用的是 <see cref="HostKeyConfirmDialog"/>；批量若也逐台弹，选 8 台新机器就是 8 个模态框。
/// 所以这里把探测阶段收集到的指纹一次性列出来，让用户逐条看一眼再统一决定。
/// </para>
/// <para>
/// <b>本框里不会出现"指纹与本机记录不一致"的项</b> —— 那是中间人信号，批量刻意不处理，
/// 由 <c>SshBatchConnector</c> 直接标为失败，用户要连就单独去连、那时弹现有的强警告框。
/// 把安全事件和常规操作混在同一次确认里，最容易的后果是用户一路点"确认"。
/// </para>
/// </remarks>
public partial class BatchHostKeyConfirmDialog : CfDialogWindow
{
    public BatchHostKeyConfirmDialog(IReadOnlyList<SshHostKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        InitializeComponent();

        IntroText.Text = $"本次连接涉及 {keys.Count} 台本机尚未记录过的主机。请逐条核对指纹后再决定是否信任。";

        KeyList.ItemsSource = keys
            .Select(key => new HostKeyRow($"{key.Host}:{key.Port}", key.KeyAlgorithm, key.Fingerprint))
            .ToList();
    }

    /// <summary>
    /// 列表行。<b>指纹是公开信息</b>（远端在握手时主动出示），这里不含任何秘密 ——
    /// <c>KnownHostStore</c> 的文件注释也是这么写的。
    /// </summary>
    private sealed record HostKeyRow(string Target, string Algorithm, string Fingerprint);

    private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
