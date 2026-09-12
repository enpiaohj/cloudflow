using System.Collections.ObjectModel;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;
using CloudFlow.Azure.Auth;
using CloudFlow.Azure.Arm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// Settings 页：Accounts（MSAL 登录）/ Azure Configuration 状态 / About。
/// 对应设计文档 §6 Account Management。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAccountSessionManager _sessionManager;
    private readonly ISubscriptionDiscoveryService _subscriptionDiscovery;
    private readonly ScopeContext _scopeContext;
    private readonly MsalAuthConfig _authConfig;
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private ObservableCollection<string> _savedScopeNames = [];

    [ObservableProperty]
    private string _authStatusText = "Not configured";

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

    public string AppVersion => "CloudFlow v0.1.0-dev · 演示模式（模拟数据）";

    public string SignInButtonText => IsSigningIn ? "正在登录…" : "使用 Microsoft 登录";

    public SettingsViewModel(
        IAccountSessionManager sessionManager,
        ISubscriptionDiscoveryService subscriptionDiscovery,
        ScopeContext scopeContext,
        MsalAuthConfig authConfig,
        ShellViewModel shell)
    {
        _sessionManager = sessionManager;
        _subscriptionDiscovery = subscriptionDiscovery;
        _scopeContext = scopeContext;
        _authConfig = authConfig;
        _shell = shell;

        UpdateAuthStatus();
        _scopeContext.ScopeChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateSignedInAccount);
    }

    public Task RefreshAsync()
    {
        SavedScopeNames = [.. _scopeContext.SavedScopes.Select(s => s.Name)];
        UpdateAuthStatus();
        UpdateSignedInAccount();
        return Task.CompletedTask;
    }

    private void UpdateAuthStatus()
    {
        if (_sessionManager.IsConfigured)
        {
            AuthStatusText = "已配置";
            ClientIdDisplay = Mask(_authConfig.ClientId);
            TenantIdDisplay = _authConfig.TenantId;
        }
        else
        {
            AuthStatusText = "未配置";
            ClientIdDisplay = "—";
            TenantIdDisplay = "—";
        }
    }

    private void UpdateSignedInAccount()
    {
        SignedInAs = _scopeContext.ActiveAccount is { } account
            ? $"{account.DisplayName} ({account.Username})"
            : "（未登录，当前为 Demo 账户）";
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!_sessionManager.IsConfigured)
        {
            MessageSeverity = "Warning";
            MessageText = "Azure App Registration 未配置：请复制 src/CloudFlow.App/appsettings.example.json 为 appsettings.json，填入 ClientId 后重启应用。";
            return;
        }

        IsSigningIn = true;
        MessageText = null;
        OnPropertyChanged(nameof(SignInButtonText));
        try
        {
            // 1. MSAL 交互登录
            var account = await _sessionManager.AddAccountAsync();
            _scopeContext.SetActiveAccount(account);

            // 2. 订阅发现（含多 Tenant 识别，设计文档 §72）
            var session = await _sessionManager.GetActiveSessionAsync();
            var subscriptions = session is null
                ? []
                : await _subscriptionDiscovery.DiscoverAsync(session);

            // 3. 通知 Shell 重建账户 / Scope 选项（触发全页面真实数据刷新）
            _shell.OnSignedIn(account, subscriptions);

            UpdateSignedInAccount();
            MessageSeverity = "Succeeded";
            MessageText = $"登录成功：{account.Username}，发现 {subscriptions.Count} 个订阅。虚拟机列表已切换为真实数据。";
        }
        catch (Exception ex)
        {
            MessageSeverity = "Failed";
            MessageText = $"登录失败：{ex.Message}";
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
