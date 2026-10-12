using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.Themes;
using CloudFlow.App.ViewModels;
using CloudFlow.Terminal.Security;
using Microsoft.Win32;

namespace CloudFlow.App.Views;

/// <summary>
/// 凭据的新建 / 编辑对话框（设置页「凭据管理」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>密码框不参与数据绑定</b>：<c>PasswordBox.Password</c> 不是依赖属性，
/// 绑不了也不该绑 —— 一旦绑进某个 VM 属性，明文就会随属性变更通知散落到
/// 绑定引擎能触及的地方。这里只在点「保存」的瞬间读一次，随后立即 <c>Clear()</c>。
/// </para>
/// <para>
/// <b>"保持"与"清除"是两个不同的意图，不能靠"框空着"表示。</b>
/// 空密码框的默认含义是「不修改」（否则每次改个备注都会把密码清掉），
/// 所以"清除"必须由用户显式勾选复选框来表达，那两个复选框只在确实存有对应
/// 秘密且认证方式匹配时才出现。
/// </para>
/// </remarks>
public partial class CredentialEditorDialog : CfDialogWindow
{
    private readonly SshCredentialService _library;
    private readonly SshCredential? _existing;

    public CredentialEditorDialog(SshCredentialService library, SshCredential? existing)
    {
        ArgumentNullException.ThrowIfNull(library);

        InitializeComponent();
        _library = library;
        _existing = existing;

        if (existing is null)
        {
            Title = "新建凭据";
            HeadingText.Text = "新建凭据";

            // 默认私钥方式：Azure VM 创建时下发的密钥通常就在本机 .ssh 下
            AuthTypeBox.SelectedIndex = 1;
        }
        else
        {
            Title = "编辑凭据";
            HeadingText.Text = $"编辑凭据 · {existing.Name}";

            NameBox.Text = existing.Name;
            UsernameBox.Text = existing.Username;
            DescriptionBox.Text = existing.Description;
            AuthTypeBox.SelectedIndex = existing.AuthType == SshAuthType.Password ? 0 : 1;
            KeyPathBox.Text = existing.PrivateKeyPath ?? string.Empty;
        }

        UpdateAuthPanels();
    }

    /// <summary>对话框结果；「取消」或保存失败时为 <c>null</c>。</summary>
    public CredentialEditResult? Result { get; private set; }

    private bool IsPasswordMode => AuthTypeBox.SelectedIndex == 0;

    private void AuthTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAuthPanels();

    private void KeyPathBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateKeyHint();

    private void UpdateAuthPanels()
    {
        // XAML 解析期就会走到这里（ComboBox 建项、TextBox 设 Text 都会触发），
        // 此时后面板里的控件尚未实例化 —— 不挡住就是 NullReferenceException。
        if (PasswordPanel is null || PrivateKeyPanel is null)
        {
            return;
        }

        var isPassword = IsPasswordMode;
        PasswordPanel.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;

        UpdateSecretHint();
        UpdateKeyHint();
    }

    /// <summary>密码槽位的提示与"清除"出口：只在这条凭据确实存着密码时出现。</summary>
    private void UpdateSecretHint()
    {
        // 本方法有两个调用来源（XAML 事件 + 构造函数），不能靠"调用时控件一定建好了"，
        // 因为 XAML 是按文档顺序逐个实例化的：靠后的控件在靠前控件的事件里必然还是 null。
        if (SecretHint is null || ClearSecretBox is null)
        {
            return;
        }

        var hasStored = _existing is { AuthType: SshAuthType.Password, SecretReference: not null };

        ClearSecretBox.Visibility = hasStored ? Visibility.Visible : Visibility.Collapsed;
        if (!hasStored)
        {
            ClearSecretBox.IsChecked = false;
        }

        SecretHint.Visibility = hasStored ? Visibility.Visible : Visibility.Collapsed;
        SecretHint.Text = "已保存密码。留空表示不修改；填入新值则替换。";
    }

    /// <summary>
    /// 私钥槽位的提示。必须如实说明本次保存<b>会不会</b>替换保险库里的私钥正文 ——
    /// 用户以为换了密钥、实际沿用了旧的，是这类功能里最难排查的一种失败。
    /// </summary>
    private void UpdateKeyHint()
    {
        // ClearPassphraseBox 在 XAML 里声明在 KeyHint 之后，所以这两个必须一起判 ——
        // 只判 KeyHint 的话，守卫本身就成了一个依赖声明顺序的假设。
        if (KeyHint is null || ClearPassphraseBox is null)
        {
            return;
        }

        // 认证方式不是私钥时这一整个面板都被收起来了，提示没有意义
        if (IsPasswordMode)
        {
            ClearPassphraseBox.Visibility = Visibility.Collapsed;
            ClearPassphraseBox.IsChecked = false;
            KeyHint.Visibility = Visibility.Collapsed;
            return;
        }

        var path = KeyPathBox.Text.Trim();
        var resolves = path.Length > 0 && File.Exists(path);
        var hasStoredKey = _existing is { AuthType: SshAuthType.PrivateKey, KeyReference: not null };

        KeyHint.Text = (path.Length, resolves, hasStoredKey) switch
        {
            (0, _, true) => "留空表示沿用保险库中已保存的私钥内容。",
            (0, _, false) => "请选择私钥文件 —— 保存时必须有一份私钥正文。",
            (_, true, _) => "保存时会把该文件的内容读入并加密存入保险库，替换原有的私钥。",
            (_, false, true) => "该文件不存在，保存时不会替换保险库中已保存的私钥内容（凭据仍可用）。",
            _ => "该文件不存在，请重新选择。"
        };
        KeyHint.Visibility = Visibility.Visible;

        // passphrase 的"清除"出口：仅当认证方式为私钥且确曾存过 passphrase 时
        var hasStoredPassphrase =
            _existing is { AuthType: SshAuthType.PrivateKey, SecretReference: not null };
        ClearPassphraseBox.Visibility = hasStoredPassphrase ? Visibility.Visible : Visibility.Collapsed;
        if (!hasStoredPassphrase)
        {
            ClearPassphraseBox.IsChecked = false;
        }
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// <c>async void</c> 是 WPF 事件处理器的正常形态，但<b>异常绝不能逸出</b>：
    /// 逸出到 Dispatcher 就是一个未捕获异常，会直接放倒整个应用。
    /// 所以下面把 await 全包在 try 里，失败一律转成对话框内的错误行。
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            // 异常消息里只有路径与文件系统原因，不含任何秘密
            ShowError($"保存失败：{ex.Message}");
        }
    }

    private async Task SaveAsync()
    {
        HideError();

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("请填写凭据名称。");
            return;
        }

        var username = UsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            ShowError("请填写用户名。");
            return;
        }

        if (await _library.IsNameTakenAsync(name, _existing?.Id))
        {
            ShowError($"已存在同名凭据「{name}」。请换一个名称。");
            return;
        }

        var isPassword = IsPasswordMode;
        var authType = isPassword ? SshAuthType.Password : SshAuthType.PrivateKey;

        // ── 密码 / passphrase 槽位 ──
        // 三态：null = 保持（框空着）、"" = 清除（显式勾选）、非空 = 替换。
        string? secret;
        if (isPassword)
        {
            secret = ClearSecretBox.IsChecked is true && _existing is not null
                ? string.Empty
                : SecretBox.Password.Length > 0 ? SecretBox.Password : null;
        }
        else
        {
            secret = ClearPassphraseBox.IsChecked is true && _existing is not null
                ? string.Empty
                : PassphraseBox.Password.Length > 0 ? PassphraseBox.Password : null;
        }

        if (isPassword && secret is null &&
            _existing is not { AuthType: SshAuthType.Password, SecretReference: not null })
        {
            // 没有可"保持"的旧值：新建、或刚从私钥方式切过来
            ShowError("密码方式必须填写密码。");
            return;
        }

        // ── 私钥正文槽位 ──
        string? keyBody = null;
        if (!isPassword)
        {
            var path = KeyPathBox.Text.Trim();
            if (path.Length > 0 && File.Exists(path))
            {
                keyBody = await File.ReadAllTextAsync(path);
            }

            if (keyBody is null &&
                _existing is not { AuthType: SshAuthType.PrivateKey, KeyReference: not null })
            {
                ShowError("私钥方式必须选择一个存在的私钥文件。");
                return;
            }
        }

        // 只填界面字段；引用键由服务层从库里读取后自行装配 ——
        // 入参里带引用字段是没用的（服务层刻意不信它），带上了反而像是有意义。
        var draft = new SshCredential
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = name,
            Username = username,
            AuthType = authType,
            PrivateKeyPath = isPassword || KeyPathBox.Text.Trim().Length == 0
                ? null
                : KeyPathBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
        };

        // 明文已进入 Result，缩短它在控件里的存活时间
        SecretBox.Clear();
        PassphraseBox.Clear();

        Result = new CredentialEditResult
        {
            Credential = draft,
            Secret = secret,
            PrivateKeyBody = keyBody
        };

        DialogResult = true;
    }

    private void ShowError(string message) => ErrorBanner.Text = message;

    private void HideError() => ErrorBanner.Text = string.Empty;
}
