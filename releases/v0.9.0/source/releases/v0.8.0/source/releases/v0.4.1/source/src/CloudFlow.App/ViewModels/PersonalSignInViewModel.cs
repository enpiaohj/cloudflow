using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CloudFlow.Azure.Identity;
using CloudFlow.Core.Identity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 个人 Microsoft 账户设备码登录（嵌入式 Azure CLI 流）。
///
/// 设计要点：登录入口不绑定任何页面 —— 顶栏账户菜单与设置页都调用本 ViewModel 的
/// <see cref="RunAsync"/>，由 Shell 在应用当前页面上以遮罩形式呈现设备码，
/// 用户无需先跳到设置页。
/// </summary>
public partial class PersonalSignInViewModel : ObservableObject
{
    /// <summary>设备码流的固定验证网址；CLI 输出解析失败时作为兜底展示。</summary>
    private const string DefaultVerificationUrl = "https://microsoft.com/devicelogin";

    private static readonly Regex VerificationUrlPattern =
        new(@"https://\S*microsoft\.com/devicelogin\S*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeviceCodePattern =
        new(@"code\s+([A-Za-z0-9]{6,12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CloudAccountDirectory _directory;
    private readonly StringBuilder _rawOutput = new();
    private readonly object _rawOutputLock = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    private string _deviceCode = "";

    [ObservableProperty]
    private string _verificationUrl = DefaultVerificationUrl;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private string? _noticeText;

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>登录流程进行中（含登录成功后的订阅发现阶段）：显示进度条。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>设备码登录阶段（仅此时可取消；订阅发现阶段取消失效，故不展示取消按钮）。</summary>
    [ObservableProperty]
    private bool _canCancel;

    public bool HasDeviceCode => DeviceCode.Length > 0;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public PersonalSignInViewModel(CloudAccountDirectory directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// 启动设备码登录并等待用户完成。成功返回账户（遮罩保持打开，由调用方在切换账户后关闭）；
    /// 用户取消或被关闭时返回 null，失败时在遮罩内展示原因并返回 null。
    /// </summary>
    public async Task<CloudAccount?> RunAsync()
    {
        // 并发守卫：重复触发不得开出第二个设备码会话（否则会遗留无人认领的 CLI Profile）
        if (_isRunning)
        {
            return null;
        }

        _isRunning = true;
        Reset();
        _cts = new CancellationTokenSource();
        try
        {
            var account = await _directory
                .SignInPersonalAccountAsync(AppendOutputLine, _cts.Token)
                .ConfigureAwait(true);
            NoticeText = null;
            return account;
        }
        catch (OperationCanceledException)
        {
            Close();
            return null;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return null;
        }
        finally
        {
            CanCancel = false;
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>进入下一阶段（如登录成功后的订阅发现），保持遮罩打开并更新状态文案。</summary>
    public void SetBusy(string status)
    {
        StatusText = status;
        IsBusy = true;
        ErrorText = null;
    }

    public void ShowError(string message)
    {
        ErrorText = message;
        IsBusy = false;
        CanCancel = false;
        StatusText = "登录未完成";
    }

    public void Close()
    {
        IsVisible = false;
        IsBusy = false;
        CanCancel = false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>关闭遮罩（失败后由用户确认，避免遮罩一直停留在错误状态）。</summary>
    [RelayCommand]
    private void Dismiss() => Close();

    [RelayCommand]
    private void CopyDeviceCode() => CopyToClipboard(DeviceCode, "已复制设备码。");

    [RelayCommand]
    private void CopyVerificationUrl() => CopyToClipboard(VerificationUrl, "已复制验证网址。");

    [RelayCommand]
    private void OpenVerificationUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo(VerificationUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NoticeText = $"无法自动打开浏览器，请手动访问 {VerificationUrl}（{ex.Message}）。";
        }
    }

    private void Reset()
    {
        lock (_rawOutputLock)
        {
            _rawOutput.Clear();
        }

        DeviceCode = "";
        VerificationUrl = DefaultVerificationUrl;
        LogText = "";
        ErrorText = null;
        NoticeText = null;
        StatusText = "正在启动登录，请稍候…";
        IsBusy = true;
        CanCancel = true;
        IsVisible = true;
    }

    private void CopyToClipboard(string text, string successNotice)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            NoticeText = "当前没有可复制的内容。";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            NoticeText = successNotice;
        }
        catch (Exception ex)
        {
            NoticeText = $"复制失败，请手动选择文本复制：{ex.Message}";
        }
    }

    /// <summary>CLI 逐行输出（stdout / stderr）在后台线程到达，必须切回 UI 线程更新界面。</summary>
    private void AppendOutputLine(string line)
    {
        lock (_rawOutputLock)
        {
            _rawOutput.AppendLine(line);
        }

        Application.Current?.Dispatcher.BeginInvoke(UpdateFromOutput);
    }

    private void UpdateFromOutput()
    {
        string raw;
        lock (_rawOutputLock)
        {
            raw = _rawOutput.ToString();
        }

        LogText = raw.Trim();

        var url = VerificationUrlPattern.Match(raw);
        if (url.Success)
        {
            VerificationUrl = url.Value.TrimEnd('.', ',', ';', ')');
        }

        var code = DeviceCodePattern.Match(raw);
        if (code.Success)
        {
            DeviceCode = code.Groups[1].Value.ToUpperInvariant();
            StatusText = "请在浏览器中打开下方网址，并输入设备码完成登录。";
        }
    }
}
