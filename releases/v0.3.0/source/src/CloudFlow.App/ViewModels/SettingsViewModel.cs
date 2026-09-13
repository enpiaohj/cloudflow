using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CloudFlow.App.Infrastructure;
using CloudFlow.App.Themes;
using CloudFlow.Azure.Auth;
using CloudFlow.Azure.Identity;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Data.Stores;
using CloudFlow.Terminal.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CloudFlow.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAccountSessionManager _sessionManager;
    private readonly CloudAccountDirectory _directory;
    private readonly ScopeContext _scopeContext;
    private readonly MsalAuthConfig _authConfig;
    private readonly ShellViewModel _shell;
    private readonly AppSettingsStore _settings;
    private readonly IJobStore _jobStore;
    private readonly SshConnectionService _ssh;

    // ==================== 分节导航 ====================
    //
    // 设置页有 8 个分节，一条长滚动列会把「改主题」和「清除任务历史」混在同一个视线里，
    // 用户找一项要滚很久、也看不出总共有哪些设置。改成顶部分节条 + 下方内容。
    //
    // 分节顺序：账户排第一 —— 打开设置最先要回答的问题是「我现在是谁、怎么退出去」，
    // 这也是成熟桌面应用的通行次序（账户 → 通用偏好 → 功能设置 → 数据 → 关于）。
    // 凭据紧跟账户：它回答的是「我拿什么去连机器」，与账户同属"身份"，但彼此独立
    // （Azure 账户凭据走 MSAL，SSH 凭据走 DPAPI 保险库，名字相近而已）。

    /// <summary>设置页分节条的一项。<see cref="Key"/> 供 XAML 的 SectionVisible 转换器比较，不要改字面量。</summary>
    public sealed record SettingsSection(string Key, string Title, SymbolRegular Symbol);

    private static readonly SettingsSection[] AllSections =
    [
        new("account", "账户", SymbolRegular.Person24),
        // 图标码位必须在 U+FFFF 以内：LockClosedKey24（U+F00E1）/ HardDrive24（U+F0306）会被
        // WPF-UI 3.0.5 截断成 16 位，渲染成"á"和一个孤立的变音符（XamlSymbolLiteralTests 有码位检查）。
        new("credentials", "凭据管理", SymbolRegular.Key24),
        new("appearance", "外观", SymbolRegular.PaintBrush24),
        new("approval", "操作与审批", SymbolRegular.ShieldCheckmark24),
        new("lists", "列表与刷新", SymbolRegular.ArrowClockwise24),
        new("network", "网络", SymbolRegular.Globe24),
        new("local", "本地数据", SymbolRegular.Database24),
        new("about", "关于", SymbolRegular.Info24)
    ];

    public IReadOnlyList<SettingsSection> Sections => AllSections;

    [ObservableProperty]
    private SettingsSection _selectedSection = AllSections[0];

    /// <summary>当前分节 key。单独暴露一个字符串，免得 XAML 用 <c>SelectedSection.Key</c> 绑定 —— 那样通知链多一层。</summary>
    public string SelectedSectionKey => SelectedSection.Key;

    partial void OnSelectedSectionChanged(SettingsSection value) =>
        OnPropertyChanged(nameof(SelectedSectionKey));

    /// <summary>
    /// 正在从 <see cref="AppSettingsStore"/> 把值灌进各下拉框。
    /// 灌值会触发 <c>OnXxxChanged</c>，而那些处理函数是"用户改了"的语义 ——
    /// 不挡住的话，每次进设置页都会把刚读出来的设置原样再存一遍。
    /// </summary>
    private bool _loadingSettings;

    [ObservableProperty]
    private ObservableCollection<string> _savedScopeNames = [];

    [ObservableProperty]
    private ObservableCollection<AccountOptionViewModel> _accounts = [];

    [ObservableProperty]
    private string _authStatusText = "Not configured";

    /// <summary>徽章配色键（英文枚举，见 CfStatusBrushConverter）。显示文本走中文，配色不能复用中文串。</summary>
    [ObservableProperty]
    private string _authStatusKey = "Warning";

    [ObservableProperty]
    private string _clientIdDisplay = "—";

    [ObservableProperty]
    private string _tenantIdDisplay = "—";

    [ObservableProperty]
    private string? _messageText;

    [ObservableProperty]
    private string? _messageSeverity;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private string _signedInAs = "（未登录）";

    [ObservableProperty]
    private bool _isSignedIn;

    public string AppVersion => _scopeContext.ActiveAccount is null
        ? $"{AppInfo.DisplayName} · 演示模式（模拟数据）"
        : $"{AppInfo.DisplayName} · 已连接 Azure（真实数据）";

    public string SignInButtonText => IsSigningIn ? "正在添加账户…" : "添加工作或学校账户";

    public SettingsViewModel(
        IAccountSessionManager sessionManager,
        CloudAccountDirectory directory,
        ScopeContext scopeContext,
        MsalAuthConfig authConfig,
        ShellViewModel shell,
        AppSettingsStore settings,
        IJobStore jobStore,
        SshConnectionService ssh)
    {
        _sessionManager = sessionManager;
        _directory = directory;
        _scopeContext = scopeContext;
        _authConfig = authConfig;
        _shell = shell;
        _settings = settings;
        _jobStore = jobStore;
        _ssh = ssh;

        LoadFromSettings();
        UpdateAuthStatus();
        _scopeContext.ScopeChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateSignedInAccount);
    }

    public async Task RefreshAsync()
    {
        SavedScopeNames = [.. _scopeContext.SavedScopes.Select(scope => scope.Name)];

        // 设置可能被别的窗口或外部编辑改过，每次进页面都重新读一遍，
        // 避免界面显示的档位和实际生效的档位不是一回事
        LoadFromSettings();

        UpdateAuthStatus();
        await RefreshAccountsAsync();
        await TryLoadCredentialsAsync();
        UpdateSignedInAccount();
    }

    // ==================== 应用设置（外观 / 审批 / 列表 / 网络） ====================
    //
    // 每一项都**立即生效**：改完就 Save，不等"保存"按钮。
    // 没有"保存"按钮是有意的 —— 有按钮就会有"改了没按"的状态，
    // 而这里的每一项都是本地偏好，撤销的成本比确认的成本低。

    // ---- 外观 ----

    public IReadOnlyList<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    [ObservableProperty]
    private string _selectedTheme = "跟随系统";

    partial void OnSelectedThemeChanged(string value)
    {
        if (_loadingSettings)
        {
            return;
        }

        var kind = ThemeFrom(value);
        Save(s => s with { Theme = kind });
        ApplyTheme(kind);
    }

    /// <summary>
    /// 换主题。两步都要走：
    /// <list type="bullet">
    /// <item><see cref="CfThemeManager.Apply"/> 换调色板与 WPF-UI 主题，画刷**就地改色**，已绑定的界面自动重绘。</item>
    /// <item><see cref="CfThemeManager.WatchSystemTheme"/> 重新决定要不要挂系统主题钩子 ——
    /// 从「深色」切回「跟随系统」时必须重新挂上，否则用户以为在跟随，实际纹丝不动。</item>
    /// </list>
    /// 窗口传的是主窗口：SystemThemeWatcher 要在窗口句柄上装消息钩子，没有窗口就挂不上。
    /// </summary>
    private static void ApplyTheme(AppThemeKind kind)
    {
        CfThemeManager.Apply(kind);
        if (System.Windows.Application.Current?.MainWindow is { } window)
        {
            CfThemeManager.WatchSystemTheme(window);
        }
    }

    // ---- 操作与审批 ----

    public IReadOnlyList<string> ApprovalPolicyOptions { get; } = ["关闭", "仅高危", "所有写操作"];

    [ObservableProperty]
    private string _selectedApprovalPolicy = "仅高危";

    partial void OnSelectedApprovalPolicyChanged(string value)
    {
        if (_loadingSettings)
        {
            return;
        }

        Save(s => s with { ApprovalPolicy = ApprovalFrom(value) });
    }

    // ---- 列表与刷新 ----

    public IReadOnlyList<string> RefreshIntervalOptions { get; } =
        [.. AppSettings.SupportedAutoRefreshSeconds.Select(RefreshText)];

    [ObservableProperty]
    private string _selectedRefreshInterval = RefreshText(0);

    partial void OnSelectedRefreshIntervalChanged(string value)
    {
        if (_loadingSettings)
        {
            return;
        }

        Save(s => s with { AutoRefreshSeconds = RefreshSecondsFrom(value) });
    }

    public IReadOnlyList<string> PageSizeOptions { get; } =
        [.. AppSettings.SupportedPageSizes.Select(size => $"{size} 条/页")];

    [ObservableProperty]
    private string _selectedPageSize = "10 条/页";

    partial void OnSelectedPageSizeChanged(string value)
    {
        if (_loadingSettings)
        {
            return;
        }

        Save(s => s with { DefaultPageSize = PageSizeFrom(value) });
    }

    // ---- 网络 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkDisclosure))]
    private bool _autoDetectPublicIp = true;

    /// <summary>
    /// 「关于」里的对外请求披露。**按开关的真实状态生成**。
    ///
    /// 为什么不能写死一句"不上报任何遥测"：那句话本身是对的（确实没有遥测），
    /// 但只说它就会盖住另一半事实 —— 开着自动探测时，应用确实会向 api.ipify.org
    /// 发一次请求。把"没有遥测"当成"没有任何对外请求"来读，是被文案误导的，
    /// 而这是用户唯一能知道这件事的地方。
    /// </summary>
    public string NetworkDisclosure => AutoDetectPublicIp
        ? "CloudFlow 不收集遥测、崩溃报告或使用统计，不会向 Microsoft 或任何第三方发送使用数据。"
          + "唯一的对外请求来自「自动查询我的公网 IP」：新建入站规则时向 https://api.ipify.org 发起一次 HTTPS 请求"
          + "以获取本机出口 IP（失败时改用 https://icanhazip.com），可在「网络」设置中关闭。"
        : "CloudFlow 不收集遥测、崩溃报告或使用统计，不会向 Microsoft 或任何第三方发送使用数据。"
          + "「自动查询我的公网 IP」已关闭，应用不会发出任何对外请求；新建入站规则时需手动填写来源地址。";

    partial void OnAutoDetectPublicIpChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        Save(s => s with { AutoDetectPublicIp = value });
    }

    // ---- 本地数据与危险区 ----

    public string DataDirectory => CloudFlowPaths.Root;

    /// <summary>本地数据区的反馈。与账户卡的 <see cref="MessageText"/> 分开：
    /// 危险区在页面最底下，把结果显示在页面顶部的账户卡里等于让用户来回找。</summary>
    [ObservableProperty]
    private string? _dataMessageText;

    [ObservableProperty]
    private string? _dataMessageSeverity;

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            // 首次运行时目录还没建出来（设置没写过盘就不会有），先建再打开，
            // 否则资源管理器会弹一个"找不到"的错误框
            Directory.CreateDirectory(CloudFlowPaths.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = CloudFlowPaths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DataMessageSeverity = "Failed";
            DataMessageText = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 清除本地任务历史。只清 Job，**不碰**账户登录状态、已保存的 Scope、审计日志 ——
    /// 确认框里把这三样逐条写出来，是因为"清除"这两个字太容易被理解成"清空全部本地数据"。
    /// </summary>
    [RelayCommand]
    private async Task ClearJobHistoryAsync()
    {
        var count = _jobStore.GetAll().Count;
        if (count == 0)
        {
            DataMessageSeverity = "Info";
            DataMessageText = "本地没有任务历史，无需清除。";
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            $"将删除本机保存的 {count} 条任务历史（jobs.json）。\n\n" +
            "不会删除：账户登录状态、已保存的 Scope、审计日志 audit.jsonl。\n\n" +
            "此操作不可撤销。",
            "清除本地任务历史",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _jobStore.ClearAsync();
            // 任务中心那份列表是从同一个 Store 读的，不叫它刷新的话，
            // 用户切过去还能看到一份已经不在磁盘上的历史
            await _shell.RefreshJobsAsync();
            DataMessageSeverity = "Succeeded";
            DataMessageText = $"已清除 {count} 条任务历史。";
        }
        catch (Exception ex)
        {
            DataMessageSeverity = "Failed";
            DataMessageText = $"清除失败：{ex.Message}";
        }
    }

    // ==================== 凭据管理 ====================
    //
    // 凭据是**全局资产**：一条凭据可用于任意 VM，不绑定主机。
    // 它自己的分节而不是塞进「账户」，是因为两者虽同属"身份"，机制却完全独立 ——
    // Azure 账户走 MSAL，SSH 凭据走 DPAPI 保险库，互不影响、也互不共享。

    /// <summary>凭据列表（按名称排序，与连接框下拉共用同一个次序）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCredentials))]
    private ObservableCollection<CredentialRowViewModel> _credentials = [];

    /// <summary>是否还没有任何凭据（驱动空态文案）。列表每次整体替换，跟着它通知即可。</summary>
    public bool HasNoCredentials => Credentials.Count == 0;

    /// <summary>
    /// 凭据区的反馈条。与账户卡的 <see cref="MessageText"/>、本地数据区的
    /// <see cref="DataMessageText"/> 都分开：三张卡片各在一屏，结果必须出现在
    /// 触发它的那张卡里，否则用户要来回滚动找一句话。
    /// </summary>
    [ObservableProperty]
    private string? _credentialMessageText;

    [ObservableProperty]
    private string? _credentialMessageSeverity;

    /// <summary>
    /// 把库里的凭据读进列表；读失败时清空列表、把原因写进本卡反馈条并返回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 首次调用会顺带触发旧格式迁移（<c>GetAllAsync</c> 内部保证），
    /// 于是"设置页能看到迁移来的凭据"不需要任何额外的启动逻辑。
    /// </remarks>
    private async Task<bool> TryLoadCredentialsAsync()
    {
        try
        {
            var all = await _ssh.CredentialLibrary.GetAllAsync();
            Credentials = new ObservableCollection<CredentialRowViewModel>(
                all.Select(credential => new CredentialRowViewModel { Credential = credential }));
            return true;
        }
        catch (Exception ex)
        {
            // 库文件损坏、或由更高版本写入：这是一句用户能据以行动的提示，不该是崩溃
            Credentials = [];
            CredentialMessageSeverity = "Failed";
            CredentialMessageText = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private async Task NewCredentialAsync()
    {
        var result = ShowCredentialEditor(existing: null);
        if (result is null)
        {
            return;
        }

        await PersistCredentialAsync(
            () => _ssh.CredentialLibrary.CreateAsync(
                result.Credential, result.Secret, result.PrivateKeyBody),
            "已新建凭据");
    }

    [RelayCommand]
    private async Task EditCredentialAsync(CredentialRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // 从库里重读一次，不用行上那份快照：列表可能是几分钟前加载的，
        // 而编辑对话框要拿它判断"是否有可保持的旧秘密"，用陈旧数据会判断错。
        SshCredential? current;
        try
        {
            current = await _ssh.CredentialLibrary.GetAsync(row.Id);
        }
        catch (Exception ex)
        {
            CredentialMessageSeverity = "Failed";
            CredentialMessageText = ex.Message;
            return;
        }

        if (current is null)
        {
            CredentialMessageSeverity = "Warning";
            CredentialMessageText = $"凭据「{row.Name}」已不存在，列表已刷新。";
            await TryLoadCredentialsAsync();
            return;
        }

        var result = ShowCredentialEditor(current);
        if (result is null)
        {
            return;
        }

        await PersistCredentialAsync(
            () => _ssh.CredentialLibrary.UpdateAsync(
                result.Credential, result.Secret, result.PrivateKeyBody),
            "已保存凭据");
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync(CredentialRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            // 删除前先把影响面算出来并告知 —— 照 RemoteFlow 的做法：**只告知，不阻止**。
            // 阻止删除会让用户没法清理一条在别处被引用的凭据，而"清空引用"本身是无损的：
            // 那些 VM 回到「未指定凭据」，下次连接重新选即可。
            var referenced = await _ssh.CredentialLibrary.CountVmsUsingCredentialAsync(row.Id);

            var impact = referenced == 0
                ? "已经建立的终端会话不受影响；下次连接该主机时需要重新提供凭据。"
                : $"有 {referenced} 台虚拟机记住了这条凭据，删除后它们会回到「未指定凭据」，下次连接时需重新选择。";

            if (!ConfirmDangerous(
                    $"确定要删除凭据「{row.Name}」吗？\n\n" +
                    "它保存的密码或私钥会一并从本机保险库中清除，此操作无法撤销。\n" +
                    impact,
                    "删除凭据"))
            {
                return;
            }

            await _ssh.CredentialLibrary.DeleteAsync(row.Id);
            if (await TryLoadCredentialsAsync())
            {
                CredentialMessageSeverity = "Succeeded";
                CredentialMessageText = $"已删除凭据「{row.Name}」及其保存的密码或私钥。";
            }
        }
        catch (Exception ex)
        {
            CredentialMessageSeverity = "Failed";
            CredentialMessageText = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>新建与编辑共用的落库路径。<paramref name="persist"/> 失败时把原因写进本卡反馈条。</summary>
    private async Task PersistCredentialAsync(
        Func<Task<SshCredential>> persist, string successPrefix)
    {
        try
        {
            var saved = await persist();

            // 成功后才刷新：先刷会把失败的原因冲掉，用户只看到列表没变、却不知道为何
            if (await TryLoadCredentialsAsync())
            {
                CredentialMessageSeverity = "Succeeded";
                CredentialMessageText = $"{successPrefix}「{saved.Name}」。";
            }
        }
        catch (Exception ex)
        {
            CredentialMessageSeverity = "Failed";
            CredentialMessageText = ex.Message;
        }
    }

    private CredentialEditResult? ShowCredentialEditor(SshCredential? existing)
    {
        var dialog = new Views.CredentialEditorDialog(_ssh.CredentialLibrary, existing)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        return dialog.ShowDialog() is true ? dialog.Result : null;
    }

    /// <summary>危险操作确认框。<b>默认按钮是「取消」</b> —— 顺手一个回车不该是最危险的那个结果。</summary>
    private static bool ConfirmDangerous(string text, string caption)
    {
        const System.Windows.MessageBoxButton buttons = System.Windows.MessageBoxButton.OKCancel;
        const System.Windows.MessageBoxImage icon = System.Windows.MessageBoxImage.Warning;
        const System.Windows.MessageBoxResult safeDefault = System.Windows.MessageBoxResult.Cancel;

        return System.Windows.Application.Current?.MainWindow is { } owner
            ? System.Windows.MessageBox.Show(owner, text, caption, buttons, icon, safeDefault)
                == System.Windows.MessageBoxResult.OK
            : System.Windows.MessageBox.Show(text, caption, buttons, icon, safeDefault)
                == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>把当前生效的设置灌进各下拉框/开关。全程置位 <see cref="_loadingSettings"/>。</summary>
    private void LoadFromSettings()
    {
        _loadingSettings = true;
        try
        {
            var current = _settings.Current;
            SelectedTheme = ThemeText(current.Theme);
            SelectedApprovalPolicy = ApprovalText(current.ApprovalPolicy);
            SelectedRefreshInterval = RefreshText(current.AutoRefreshSeconds);
            SelectedPageSize = $"{current.DefaultPageSize} 条/页";
            AutoDetectPublicIp = current.AutoDetectPublicIp;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void Save(Func<AppSettings, AppSettings> mutate) =>
        _settings.Save(mutate(_settings.Current));

    // 文案 ↔ 值 的双向映射。分布在各处直接写字面量的话，
    // 加一档就会漏掉其中一处（显示对了但存下去的档位不对，反之亦然）。

    private static string ThemeText(AppThemeKind kind) => kind switch
    {
        AppThemeKind.Light => "浅色",
        AppThemeKind.Dark => "深色",
        _ => "跟随系统"
    };

    private static AppThemeKind ThemeFrom(string text) => text switch
    {
        "浅色" => AppThemeKind.Light,
        "深色" => AppThemeKind.Dark,
        _ => AppThemeKind.System
    };

    private static string ApprovalText(ApprovalPolicy policy) => policy switch
    {
        ApprovalPolicy.Off => "关闭",
        ApprovalPolicy.AllWrites => "所有写操作",
        _ => "仅高危"
    };

    private static ApprovalPolicy ApprovalFrom(string text) => text switch
    {
        "关闭" => ApprovalPolicy.Off,
        "所有写操作" => ApprovalPolicy.AllWrites,
        _ => ApprovalPolicy.HighRiskOnly
    };

    private static string RefreshText(int seconds) => seconds switch
    {
        30 => "30 秒",
        60 => "1 分钟",
        300 => "5 分钟",
        _ => "关闭"
    };

    private static int RefreshSecondsFrom(string text) => text switch
    {
        "30 秒" => 30,
        "1 分钟" => 60,
        "5 分钟" => 300,
        _ => 0
    };

    private static int PageSizeFrom(string text) =>
        int.TryParse(text.Split(' ')[0], out var size) && AppSettings.SupportedPageSizes.Contains(size)
            ? size
            : 10;

    private async Task RefreshAccountsAsync()
    {
        var accounts = await _directory.GetAccountsAsync();
        Accounts = new ObservableCollection<AccountOptionViewModel>(accounts.Select(account =>
            new AccountOptionViewModel { Account = account }));
    }

    private void UpdateAuthStatus()
    {
        if (_sessionManager.IsConfigured)
        {
            AuthStatusText = "已配置";
            AuthStatusKey = "Protected";
            ClientIdDisplay = Mask(_authConfig.ClientId);
            TenantIdDisplay = _authConfig.TenantId;
        }
        else
        {
            AuthStatusText = "未配置";
            AuthStatusKey = "Warning";
            ClientIdDisplay = "—";
            TenantIdDisplay = "—";
        }
    }

    private void UpdateSignedInAccount()
    {
        IsSignedIn = _scopeContext.ActiveAccount is not null;
        SignedInAs = _scopeContext.ActiveAccount is { } account
            ? $"当前账户：{account.DisplayName} ({account.Username})"
            : "（未登录，当前为 Demo 模式）";
        OnPropertyChanged(nameof(AppVersion));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!_sessionManager.IsConfigured)
        {
            MessageSeverity = "Warning";
            MessageText = "CloudFlow 登录尚未配置，请联系应用管理员。";
            return;
        }

        IsSigningIn = true;
        MessageText = null;
        OnPropertyChanged(nameof(SignInButtonText));
        try
        {
            var account = await _shell.AddAccountAsync();
            await RefreshAccountsAsync();
            UpdateSignedInAccount();
            MessageSeverity = "Succeeded";
            MessageText = $"已添加并切换到 {account.Username}，发现 {_scopeContext.AvailableSubscriptions.Count} 个订阅。";
        }
        catch (Exception ex)
        {
            MessageSeverity = "Failed";
            MessageText = $"添加账户失败：{ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            OnPropertyChanged(nameof(SignInButtonText));
        }
    }

    /// <summary>
    /// 添加个人 Microsoft 账户（嵌入式 Azure CLI 设备码流）。
    /// 设备码遮罩由 Shell 直接在当前页面弹出，取消与失败原因都在遮罩内呈现。
    /// </summary>
    [RelayCommand]
    private async Task AddPersonalAccountAsync()
    {
        MessageText = null;
        if (!await _shell.AddPersonalAccountInteractiveAsync())
        {
            return;
        }

        await RefreshAccountsAsync();
        UpdateSignedInAccount();
        MessageSeverity = "Succeeded";
        MessageText = $"已添加并切换到 {_scopeContext.ActiveAccount?.Username}，发现 {_scopeContext.AvailableSubscriptions.Count} 个订阅。";
    }

    [RelayCommand]
    private Task SignOutAsync() => _scopeContext.ActiveAccount is { } account
        ? RemoveAccountAsync(account)
        : Task.CompletedTask;

    [RelayCommand]
    private async Task RemoveAccountAsync(CloudAccount? account)
    {
        if (account is null || IsSigningIn)
        {
            return;
        }

        IsSigningIn = true;
        MessageText = null;
        OnPropertyChanged(nameof(SignInButtonText));
        try
        {
            await _directory.RemoveAccountAsync(account);
            await _shell.OnAccountRemovedAsync(account.AccountId);
            await RefreshAccountsAsync();
            UpdateSignedInAccount();
            MessageSeverity = "Succeeded";
            MessageText = "已从此设备移除账户登录缓存。";
        }
        catch (Exception ex)
        {
            MessageSeverity = "Failed";
            MessageText = $"移除账户失败：{ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            OnPropertyChanged(nameof(SignInButtonText));
        }
    }

    private static string Mask(string value)
    {
        if (value.Length <= 10)
        {
            return value;
        }
        return $"{value[..6]}…{value[^4..]}";
    }
}
