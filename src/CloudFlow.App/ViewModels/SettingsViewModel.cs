using System.Collections.ObjectModel;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;
using CloudFlow.Azure.Auth;
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
    private readonly ScopeContext _scopeContext;
    private readonly MsalAuthConfig _authConfig;

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

    public string AppVersion => $"CloudFlow v0.1.0-dev  ·  Demo 模式（Mock 数据）";

    public string SignInButtonText => IsSigningIn ? "Signing in…" : "Sign in with Microsoft";

    public SettingsViewModel(
        IAccountSessionManager sessionManager,
        ScopeContext scopeContext,
        MsalAuthConfig authConfig)
    {
        _sessionManager = sessionManager;
        _scopeContext = scopeContext;
        _authConfig = authConfig;

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
            AuthStatusText = "Configured";
            ClientIdDisplay = Mask(_authConfig.ClientId);
            TenantIdDisplay = _authConfig.TenantId;
        }
        else
        {
            AuthStatusText = "Not configured";
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
            var account = await _sessionManager.AddAccountAsync();
            _scopeContext.SetActiveAccount(account);
            UpdateSignedInAccount();
            MessageSeverity = "Succeeded";
            MessageText = $"登录成功：{account.Username}";
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
