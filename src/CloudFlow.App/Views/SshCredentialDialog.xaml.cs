using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.Infrastructure;
using CloudFlow.App.Themes;
using CloudFlow.Terminal.Security;
using Microsoft.Win32;

namespace CloudFlow.App.Views;

/// <summary>
/// SSH 连接凭据。两条<b>互斥</b>的出口，由下拉的选中项唯一决定：
/// <list type="bullet">
/// <item>选中凭据库里的某一条 —— 置 <see cref="SshCredentialInput.UseCredentialId"/>，
/// 由调用方从保险库解密，明文不进本对话框；</item>
/// <item>选中「临时输入」—— 用输入框里的值；勾「存入凭据库」则额外要求填名称，
/// 由调用方落库。</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// 库中凭据的密码与私钥正文<b>永不回填</b>任何控件：明文只在输入框与返回值里短暂存在。
/// 所以"用哪条凭据"必须是用户在下拉里的显式选择，不能靠"密码框空着就算复用"来猜 ——
/// 那会把"想改密码但还没填"误判成复用。
/// </para>
/// <para>
/// 上一版还有第二个信号 <c>UseSavedCredential</c>（每 VM 已保存凭据），与
/// <see cref="Result"/> 并列、由调用方自己判断两者关系。两个信号表达同一件事，
/// 却没有任何东西保证它们不冲突。现在收敛成一个：<b>出口只有 <see cref="Result"/> 一个</b>。
/// </para>
/// </remarks>
public partial class SshCredentialDialog : CfDialogWindow
{
    /// <summary>
    /// 下拉首项：它不是一个凭据，用一个 Id 为 null 的哨兵表示。
    /// 文案必须同时覆盖它的两种用法 —— 纯临时输入，以及勾「存入凭据库」后新建一条，
    /// 写成"不保存"会与下方那个复选框直接打架。
    /// </summary>
    private const string TransientLabel = "新建 / 临时输入…";

    private readonly SshCredentialService _library;

    /// <summary>目标 VM 的 ResourceId —— 用来取"这台机器上次用的那条凭据"。可为 null。</summary>
    private readonly string? _vmResourceId;

    /// <summary>下拉里可选的库中凭据，与 <see cref="CredentialChoice"/> 的 Id 一一对应。</summary>
    private List<SshCredential> _credentials = [];

    public SshCredentialDialog(
        SshCredentialService library, string host, string? vmResourceId = null, string? targetCaption = null)
    {
        ArgumentNullException.ThrowIfNull(library);

        InitializeComponent();
        _library = library;
        _vmResourceId = vmResourceId;

        // 图标走 C# 而不是 XAML 的 Symbol 字面量：XAML 那个由运行时 EnumConverter 解析、
        // 编译器不校验，写错要等首次布局才抛 XamlParseException；C# 侧是编译期检查的。
        TargetIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.WindowConsole20;

        // 批量场景下"目标"不是单一主机，由调用方给一句更准确的说明
        TargetText.Text = targetCaption ?? host;

        // 默认私钥文件方式：Azure VM 创建时的密钥通常就在本机 .ssh 下。
        // XAML 里刻意不写 IsSelected —— 那会在解析期就触发 SelectionChanged，
        // 此时后面板里的控件尚未实例化。
        AuthTypeBox.SelectedIndex = 1;
        UpdateAuthPanels();
        UpdateNamePanel();

        // 凭据库要异步读盘，构造函数里不能 await —— 等控件挂上可视树再加载
        Loaded += async (_, _) => await LoadCredentialsAsync();
    }

    /// <summary>用户在对话框里的最终输入；「取消」时为 null。</summary>
    public SshCredentialInput? Result { get; private set; }

    /// <summary>下拉项。<c>ToString()</c> 就是显示文本 —— ComboBox 默认拿它渲染。</summary>
    private sealed record CredentialChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }

    private async Task LoadCredentialsAsync()
    {
        var items = new List<CredentialChoice> { new(null, TransientLabel) };
        var preselect = 0;

        try
        {
            _credentials = [.. await _library.GetAllAsync()];
            items.AddRange(_credentials.Select(c => new CredentialChoice(c.Id, $"{c.Name}（{c.Username}）")));

            // 预选顺序（两级回退）：**这台 VM 记住的那条** → 全局最近使用的那条 → 「新建 / 临时输入…」。
            // 用户已定口径：按 VM 记住，选了凭据点「连接」就记住它，直到他改选。
            // 两个查询都会把指向已删除凭据的陈旧 id 读作 null，所以不会拿着死主键去预选。
            var preferred = _vmResourceId is null
                ? null
                : await _library.GetDefaultCredentialIdForVmAsync(_vmResourceId);

            preferred ??= await _library.GetLastUsedIdAsync();

            if (preferred is Guid targetId)
            {
                var index = items.FindIndex(item => item.Id == targetId);
                if (index >= 0)
                {
                    preselect = index;
                }
            }
        }
        catch (Exception ex)
        {
            // 库文件损坏、或由更高版本写入。这不该让人连不上机器 ——
            // 退化成"只能临时输入"，并把原因如实写在对话框里。
            _credentials = [];
            LibraryMessage.Text = $"凭据库读取失败，本次只能临时输入：{ex.Message}";
            LibraryMessage.Visibility = Visibility.Visible;
        }

        CredentialBox.ItemsSource = items;
        CredentialBox.SelectedIndex = preselect;
        UpdateMode();
    }

    private void CredentialBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMode();

    /// <summary>下拉选择 → 只读摘要 / 临时输入表单，二选一。</summary>
    private void UpdateMode()
    {
        // XAML 解析期控件尚未按文档顺序实例化完，不挡住就是 NullReferenceException
        if (SummaryPanel is null || InputPanel is null)
        {
            return;
        }

        var choice = CredentialBox.SelectedItem as CredentialChoice;
        var isTransient = choice?.Id is null;

        InputPanel.Visibility = isTransient ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = isTransient ? Visibility.Collapsed : Visibility.Visible;

        // 换了选择，上一次留在这里的报错就不再适用了
        HideError();

        if (!isTransient)
        {
            var credential = _credentials.FirstOrDefault(c => c.Id == choice!.Id);
            SummaryText.Text = credential is null
                ? "该凭据已不存在，请重新选择。"
                : Describe(credential);
        }
    }

    /// <summary>只读摘要。只说保险库里有没有密文，<b>绝不说"可用"</b> —— 能不能解开要等连接时才知道。</summary>
    private static string Describe(SshCredential credential)
    {
        var lines = new List<string>
        {
            $"{credential.Name} · 用户名 {credential.Username} · " +
            (credential.AuthType == SshAuthType.Password ? "密码认证" : "私钥认证")
        };

        // 只在私钥方式下报路径：密码方式的凭据不该带这个字段（服务层会归一化掉），
        // 万一脏数据留下了它，显示出来只会让人以为这条凭据用的是私钥。
        if (credential.AuthType == SshAuthType.PrivateKey &&
            !string.IsNullOrWhiteSpace(credential.PrivateKeyPath))
        {
            lines.Add($"私钥文件：{credential.PrivateKeyPath}");
        }

        if (!credential.HasSecretReference)
        {
            lines.Add("该凭据没有保存密码或私钥内容，连接时可能失败，请在设置页重新保存。");
        }
        else if (credential.AuthType == SshAuthType.Password)
        {
            lines.Add("密码已加密存放在本机保险库，连接时才解密，不会显示在界面上。");
        }
        else
        {
            lines.Add("私钥内容已加密存放在本机保险库，连接时才解密，不会显示在界面上。");
        }

        return string.Join("\n", lines);
    }

    private void AuthTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

    private void UpdateAuthPanels()
    {
        // XAML 解析期就会走到这里（ComboBox 建项、TextBox 设 Text 都会触发），
        // 此时后面板里的控件尚未实例化 —— 不挡住就是 NullReferenceException。
        if (PasswordPanel is null || PrivateKeyPanel is null)
        {
            return;
        }

        var isPassword = AuthTypeBox.SelectedIndex == 0;
        PasswordPanel.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveCredentialBox_Changed(object sender, RoutedEventArgs e) => UpdateNamePanel();

    private void UpdateNamePanel()
    {
        // NamePanel 在 XAML 里声明在 SaveCredentialBox <b>之后</b>，
        // 所以这两个必须一起判 —— 只判 SaveCredentialBox 的话，
        // 守卫本身就成了一个依赖声明顺序的假设。
        if (NamePanel is null)
        {
            return;
        }

        NamePanel.Visibility = SaveCredentialBox.IsChecked is true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥文件",
            Filter = "所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) is true)
        {
            KeyPathBox.Text = dialog.FileName;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        if (CredentialBox.SelectedItem is not CredentialChoice choice)
        {
            ShowError("请选择使用哪条凭据。");
            return;
        }

        // ── 出口一：用凭据库里那一条 ──
        if (choice.Id is Guid id)
        {
            Result = new SshCredentialInput
            {
                // 用户名只是把对话框的必填字段填上；服务层一律用库里那条记录的用户名
                Username = _credentials.FirstOrDefault(c => c.Id == id)?.Username ?? string.Empty,
                UseCredentialId = id,
            };

            DialogResult = true;
            return;
        }

        // ── 出口二：临时输入 ──
        var isPassword = AuthTypeBox.SelectedIndex == 0;

        var username = UsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            ShowError("请填写用户名。");
            return;
        }

        if (isPassword && SecretBox.Password.Length == 0)
        {
            ShowError("请填写密码。");
            return;
        }

        var keyPath = KeyPathBox.Text.Trim();
        if (!isPassword && (keyPath.Length == 0 || !File.Exists(keyPath)))
        {
            ShowError("请选择有效的私钥文件。");
            return;
        }

        var save = SaveCredentialBox.IsChecked is true;
        var name = NameBox.Text.Trim();
        if (save && name.Length == 0)
        {
            ShowError("勾选「存入凭据库」后必须填写凭据名称。");
            return;
        }

        Result = new SshCredentialInput
        {
            Username = username,
            AuthType = isPassword ? SshCredentialAuthType.Password : SshCredentialAuthType.PrivateKeyFile,
            // 认证方式不匹配的那个槽位一律置 null：隐藏的输入框里可能还留着
            // 用户切换方式之前敲的内容，把它带进连接只会让失败原因更难判断。
            Password = isPassword ? SecretBox.Password : null,
            PrivateKeyPath = isPassword || keyPath.Length == 0 ? null : keyPath,
            Passphrase = isPassword ? null : PassphraseBox.Password,
            SaveCredential = save,
            Name = save ? name : null,
        };

        // 明文已进入 Result，缩短它在控件里的存活时间
        SecretBox.Clear();
        PassphraseBox.Clear();

        DialogResult = true;
    }

    private void ShowError(string message) => ErrorBanner.Text = message;

    private void HideError() => ErrorBanner.Text = string.Empty;
}
