using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Azure.Identity;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;
using CloudFlow.Data.Stores;
using CloudFlow.Modules.Compute.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace CloudFlow.App.ViewModels;

public partial class ShellViewModel : ObservableObject, IShellNavigation
{
    private readonly IServiceProvider _services;
    private readonly ScopeContext _scopeContext;
    private readonly SavedScopeStore _savedScopeStore;
    private readonly ActiveAccountStore _activeAccountStore;
    private readonly IAccountSessionManager _sessions;
    private readonly ISubscriptionDiscoveryService _subscriptionDiscovery;
    private readonly CloudAccountDirectory _directory;
    private long _accountSwitchId;
    private bool _suppressAccountSelection;

    private HomeViewModel Home => _services.GetRequiredService<HomeViewModel>();
    private VirtualMachinesViewModel Vms => _services.GetRequiredService<VirtualMachinesViewModel>();
    private JobsViewModel Jobs => _services.GetRequiredService<JobsViewModel>();
    private SettingsViewModel Settings => _services.GetRequiredService<SettingsViewModel>();

    public ShellViewModel(
        IServiceProvider services,
        ScopeContext scopeContext,
        SavedScopeStore savedScopeStore,
        ActiveAccountStore activeAccountStore,
        IAccountSessionManager sessions,
        ISubscriptionDiscoveryService subscriptionDiscovery,
        CloudAccountDirectory directory,
        PersonalSignInViewModel personalSignIn,
        TerminalPanelViewModel terminal)
    {
        _services = services;
        PersonalSignIn = personalSignIn;
        Terminal = terminal;
        _scopeContext = scopeContext;
        _savedScopeStore = savedScopeStore;
        _activeAccountStore = activeAccountStore;
        _sessions = sessions;
        _subscriptionDiscovery = subscriptionDiscovery;
        _directory = directory;

        var computeGroup = new NavItemViewModel
        {
            PageKey = "compute",
            Label = "计算",
            Symbol = SymbolRegular.Cloud24,
            IsGroup = true
        };

        NavItems =
        [
            new() { PageKey = "home", Label = "首页", Symbol = SymbolRegular.Home24 },
            computeGroup,
            new() { PageKey = "vms", Label = "虚拟机", Symbol = SymbolRegular.Desktop24, IsChild = true, Parent = computeGroup },
            new() { PageKey = "jobs", Label = "任务", Symbol = SymbolRegular.Clock24 },
            new() { PageKey = "sep1", IsSeparator = true, IsEnabled = false },
            new() { PageKey = "settings", Label = "设置", Symbol = SymbolRegular.Settings24 }
        ];
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    public ObservableCollection<AccountOptionViewModel> AccountOptions { get; } = [];

    /// <summary>个人 Microsoft 账户设备码登录（顶栏账户菜单与设置页共用，不依赖当前页面）。</summary>
    public PersonalSignInViewModel PersonalSignIn { get; }

    /// <summary>底部终端会话面板。应用级唯一，SSH 会话归它所有 —— 因此切页面不会中断连接。</summary>
    public TerminalPanelViewModel Terminal { get; }

    [ObservableProperty]
    private object? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotVmDetailPage))]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeSubtitle))]
    [NotifyPropertyChangedFor(nameof(HasAccount))]
    [NotifyPropertyChangedFor(nameof(AccountInitial))]
    [NotifyPropertyChangedFor(nameof(AccountDisplayName))]
    [NotifyPropertyChangedFor(nameof(AccountToolTip))]
    private AccountOptionViewModel? _selectedAccount;

    [ObservableProperty]
    private bool _isSwitchingAccount;

    /// <summary>账户操作反馈（如恢复失败），显示在顶栏提示区。</summary>
    [ObservableProperty]
    private string? _message;

    public IReadOnlyList<string> ScopeOptions { get; private set; } = ["全部可访问订阅"];

    [ObservableProperty]
    private string _selectedScope = "全部可访问订阅";

    public string ScopeSubtitle
    {
        get
        {
            var account = SelectedAccount?.Account;
            var accountName = account is null
                ? "Demo"
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
            return $"{accountName}  |  范围：{SelectedScope}";
        }
    }

    public bool IsNotVmDetailPage => SelectedNav?.PageKey != "__detail";

    // ---- 顶栏账户区（头像 + 展示名 + 账户菜单） ----

    public bool HasAccount => SelectedAccount is not null;

    public string AccountInitial => SelectedAccount?.Initial ?? "?";

    public string AccountDisplayName => SelectedAccount?.DisplayName ?? "未登录";

    public string AccountToolTip => SelectedAccount is null
        ? "尚未登录 Microsoft 账户，当前为演示数据"
        : $"{SelectedAccount.DisplayName}\n{SelectedAccount.Subtitle}";

    public async Task InitializeAsync()
    {
        _scopeContext.SetSavedScopes(_savedScopeStore.LoadOrDefault());

        var accounts = await _directory.GetAccountsAsync().ConfigureAwait(true);
        var savedAccountId = _activeAccountStore.Load();
        var saved = accounts.FirstOrDefault(account =>
            string.Equals(account.AccountId, savedAccountId, StringComparison.Ordinal));

        // 个人 Microsoft 账户的 Token 由 CLI Profile 持有并按需刷新；
        // 启动时不需要 MSAL 静默试探，直接以该账户激活。
        if (saved is { ProviderType: AuthenticationProviderType.EmbeddedAzureCli })
        {
            RefreshAccountOptions(accounts, saved.AccountId);
            try
            {
                await SwitchAccountAsync(saved).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Profile 已被清理等：移除失效登记并回到 Demo 模式，不得显示 Mock 数据冒充真实账户
                _directory.ForgetAccount(saved.AccountId);
                accounts = await _directory.GetAccountsAsync().ConfigureAwait(true);
                RefreshAccountOptions(accounts, null);
                ApplyDemoScope();
                Message = $"上次使用的账户已不可用：{ex.Message}";
            }

            await FinishInitializationAsync();
            return;
        }

        var restored = await _sessions.TryRestoreSessionAsync(savedAccountId).ConfigureAwait(true);
        RefreshAccountOptions(accounts, restored?.Account.AccountId);
        if (restored is null)
        {
            ApplyDemoScope();
        }
        else
        {
            await SwitchAccountAsync(restored.Account, restored).ConfigureAwait(true);
        }

        await FinishInitializationAsync();
    }

    private async Task FinishInitializationAsync()
    {
        SelectedNav = NavItems.First(n => n.PageKey == "home");
        if (Current is HomeViewModel home)
        {
            await home.RefreshAsync();
        }
    }

    private void ApplyDemoScope()
    {
        _scopeContext.SetActiveAccount(null);
        _scopeContext.SetAvailableSubscriptions([]);
        BuildScopeOptions();
        SelectedScope = ScopeOptions.FirstOrDefault(o => o == "Production") ?? ScopeOptions[0];
        ApplyScope(SelectedScope);
    }

    private async Task RefreshAccountOptionsAsync(string? selectedAccountId)
    {
        var accounts = await _directory.GetAccountsAsync().ConfigureAwait(true);
        RefreshAccountOptions(accounts, selectedAccountId);
    }

    private void RefreshAccountOptions(IReadOnlyList<CloudAccount> accounts, string? selectedAccountId)
    {
        AccountOptions.Clear();
        foreach (var account in accounts)
        {
            AccountOptions.Add(new AccountOptionViewModel
            {
                Account = account,
                IsActive = string.Equals(account.AccountId, selectedAccountId, StringComparison.Ordinal)
            });
        }

        SelectAccountOption(selectedAccountId);
    }

    private void SelectAccountOption(string? accountId)
    {
        _suppressAccountSelection = true;
        SelectedAccount = AccountOptions.FirstOrDefault(option =>
            string.Equals(option.Account.AccountId, accountId, StringComparison.Ordinal));
        _suppressAccountSelection = false;
    }

    private NavItemViewModel? _lastSelectedNav;
    private bool _suppressNavSelection;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (_suppressNavSelection || value is null || value.IsSeparator)
        {
            return;
        }

        if (value.IsGroup)
        {
            value.IsExpanded = !value.IsExpanded;
            _suppressNavSelection = true;
            SelectedNav = _lastSelectedNav;
            _suppressNavSelection = false;
            return;
        }

        _lastSelectedNav = value;

        // 虚拟机列表的自动刷新只在它可见时走：在别的页面上按点调用 Azure 查询
        // 既没人看，也在持续消耗订阅的调用额度
        Vms.SetPageActive(value.PageKey == "vms");

        switch (value.PageKey)
        {
            case "home":
                Current = Home;
                break;
            case "vms":
                Current = Vms;
                _ = Vms.RefreshAsync();
                break;
            case "jobs":
                Current = Jobs;
                _ = Jobs.RefreshAsync();
                break;
            case "settings":
                Current = Settings;
                _ = Settings.RefreshAsync();
                break;
        }
    }

    partial void OnSelectedAccountChanged(AccountOptionViewModel? value)
    {
        if (_suppressAccountSelection || value is null || IsSwitchingAccount ||
            string.Equals(_scopeContext.ActiveAccount?.AccountId, value.Account.AccountId, StringComparison.Ordinal))
        {
            return;
        }

        _ = SwitchSelectedAccountAsync(value.Account);
    }

    private async Task SwitchSelectedAccountAsync(CloudAccount account)
    {
        try
        {
            await SwitchAccountAsync(account).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "CloudFlow", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            SelectAccountOption(_scopeContext.ActiveAccount?.AccountId);
        }
    }

    partial void OnSelectedScopeChanged(string value) => ApplyScope(value);

    private void ApplyScope(string option)
    {
        ResourceScope scope;
        var realSub = _scopeContext.AvailableSubscriptions.FirstOrDefault(
            s => string.Equals(s.DisplayName, option, StringComparison.OrdinalIgnoreCase));

        if (option == "全部可访问订阅")
        {
            scope = new ResourceScope { ScopeName = option, Mode = ScopeMode.AllAccessible };
        }
        else if (realSub is not null)
        {
            scope = new ResourceScope
            {
                ScopeName = option,
                Mode = ScopeMode.SingleSubscription,
                SubscriptionIds = [realSub.SubscriptionId]
            };
        }
        else
        {
            var saved = _scopeContext.SavedScopes.FirstOrDefault(s => s.Name == option);
            scope = new ResourceScope
            {
                ScopeName = option,
                Mode = saved is { SubscriptionIds.Count: 1 }
                    ? ScopeMode.SingleSubscription
                    : ScopeMode.MultipleSubscriptions,
                SubscriptionIds = saved?.SubscriptionIds ?? []
            };
        }

        _scopeContext.SetScope(scope);
        OnPropertyChanged(nameof(ScopeSubtitle));
    }

    public async Task<CloudAccount> AddAccountAsync()
    {
        var account = await _sessions.AddAccountAsync().ConfigureAwait(true);
        await SwitchAccountAsync(account).ConfigureAwait(true);
        return account;
    }

    /// <summary>
    /// 添加并切换到个人 Microsoft 账户（嵌入式 Azure CLI 设备码流）。
    /// 登录遮罩由 <see cref="PersonalSignIn"/> 在当前页面上直接弹出，不要求用户先进入设置页；
    /// 返回 true 表示登录并切换成功。
    /// </summary>
    public async Task<bool> AddPersonalAccountInteractiveAsync()
    {
        var account = await PersonalSignIn.RunAsync().ConfigureAwait(true);
        if (account is null)
        {
            return false;
        }

        // 遮罩保持打开：订阅发现期间用户应看到进度，而不是"窗口消失后卡住"
        PersonalSignIn.SetBusy("登录成功，正在读取账户订阅…");
        try
        {
            await SwitchAccountAsync(account).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PersonalSignIn.ShowError($"已登录 {account.Username}，但订阅发现失败：{ex.Message}");
            return false;
        }

        PersonalSignIn.Close();
        return true;
    }

    [RelayCommand]
    private async Task AddAccountFromShellAsync()
    {
        try
        {
            await AddAccountAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "CloudFlow", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 添加个人 Microsoft 账户：直接在当前页面弹出设备码登录遮罩，不跳转页面。
    /// </summary>
    [RelayCommand]
    private async Task AddPersonalAccountFromShellAsync()
    {
        Message = null;
        await AddPersonalAccountInteractiveAsync().ConfigureAwait(true);
    }

    /// <summary>账户菜单中切换账户（选中项即触发既有切换流程）。</summary>
    [RelayCommand]
    private void SwitchAccount(AccountOptionViewModel? option)
    {
        if (option is null || IsSwitchingAccount ||
            string.Equals(_scopeContext.ActiveAccount?.AccountId, option.Account.AccountId, StringComparison.Ordinal))
        {
            return;
        }

        SelectedAccount = option;
    }

    /// <summary>账户菜单“管理账户…”：跳到设置页账户区。</summary>
    [RelayCommand]
    private void ManageAccounts()
    {
        Message = null;
        SelectedNav = NavItems.First(n => n.PageKey == "settings");
    }

    public async Task SwitchAccountAsync(CloudAccount account, AccountSession? restoredSession = null)
    {
        var switchId = ++_accountSwitchId;
        IsSwitchingAccount = true;
        _scopeContext.SetAvailableSubscriptions([]);
        _scopeContext.SetActiveAccount(account);
        await RefreshAccountOptionsAsync(account.AccountId).ConfigureAwait(true);
        BuildScopeOptions();
        SelectedScope = "全部可访问订阅";
        ApplyScope(SelectedScope);

        try
        {
            var subscriptions = await DiscoverSubscriptionsAsync(account, restoredSession).ConfigureAwait(true);
            if (switchId != _accountSwitchId)
            {
                return;
            }

            _activeAccountStore.Save(account.AccountId);
            _scopeContext.SetAvailableSubscriptions(subscriptions);
            BuildScopeOptions();
            SelectedScope = "全部可访问订阅";
            ApplyScope(SelectedScope);
        }
        catch
        {
            if (switchId == _accountSwitchId)
            {
                _scopeContext.SetAvailableSubscriptions([]);
                BuildScopeOptions();
            }
            throw;
        }
        finally
        {
            if (switchId == _accountSwitchId)
            {
                IsSwitchingAccount = false;
            }
        }
    }

    public async Task OnAccountRemovedAsync(string accountId)
    {
        var removedActiveAccount = string.Equals(
            _scopeContext.ActiveAccount?.AccountId,
            accountId,
            StringComparison.Ordinal);

        var remainingAccounts = await _directory.GetAccountsAsync().ConfigureAwait(true);
        if (!removedActiveAccount)
        {
            await RefreshAccountOptionsAsync(_scopeContext.ActiveAccount?.AccountId).ConfigureAwait(true);
            return;
        }

        _activeAccountStore.Clear();
        _scopeContext.SetActiveAccount(null);
        _scopeContext.SetAvailableSubscriptions([]);

        var nextAccount = remainingAccounts.FirstOrDefault();
        if (nextAccount is null)
        {
            await RefreshAccountOptionsAsync(null).ConfigureAwait(true);
            ApplyDemoScope();
            return;
        }

        // 个人账户无需 MSAL 静默恢复，直接激活；企业账户沿用既有静默恢复
        if (nextAccount.ProviderType == AuthenticationProviderType.EmbeddedAzureCli)
        {
            await SwitchAccountAsync(nextAccount).ConfigureAwait(true);
            return;
        }

        var session = await _sessions.TryRestoreSessionAsync(nextAccount.AccountId).ConfigureAwait(true);
        if (session is null)
        {
            await RefreshAccountOptionsAsync(null).ConfigureAwait(true);
            ApplyDemoScope();
            return;
        }

        await SwitchAccountAsync(session.Account, session).ConfigureAwait(true);
    }

    /// <summary>
    /// 订阅发现：启动恢复时复用已验证的 MSAL 会话；
    /// 其余情况按 ProviderType 路由到对应 Provider（企业 MSAL / 个人 Embedded CLI）。
    /// </summary>
    private async Task<IReadOnlyList<SubscriptionProfile>> DiscoverSubscriptionsAsync(
        CloudAccount account,
        AccountSession? restoredSession)
    {
        if (restoredSession is not null)
        {
            return await _subscriptionDiscovery.DiscoverAsync(restoredSession).ConfigureAwait(true);
        }

        return await _directory
            .ResolveProvider(account)
            .GetSubscriptionsAsync(account)
            .ConfigureAwait(true);
    }

    private void BuildScopeOptions()
    {
        var options = new List<string> { "全部可访问订阅" };
        if (_scopeContext.AvailableSubscriptions.Count > 0)
        {
            options.AddRange(_scopeContext.AvailableSubscriptions
                .Select(s => s.DisplayName)
                .Where(n => !string.IsNullOrEmpty(n)));
        }
        else
        {
            options.AddRange(_scopeContext.SavedScopes.Select(s => s.Name));
        }

        ScopeOptions = options;
        OnPropertyChanged(nameof(ScopeOptions));
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            NavigateVirtualMachines(SearchText.Trim());
        }
        return Task.CompletedTask;
    }

    public void NavigateHome() => SelectedNav = NavItems.First(n => n.PageKey == "home");

    public void NavigateVirtualMachines(string? filter = null)
    {
        NavItems.First(n => n.PageKey == "compute").IsExpanded = true;
        var vmsItem = NavItems.First(n => n.PageKey == "vms");
        if (SelectedNav != vmsItem)
        {
            SelectedNav = vmsItem;
        }
        _lastSelectedNav = vmsItem;
        Current = Vms;
        // 这条路径可能不触发 OnSelectedNavChanged（导航项本来就是 vms，例如从详情页返回），
        // 所以可见性要在这里再声明一次，否则列表页的自动刷新会一直不恢复
        Vms.SetPageActive(true);
        if (filter is not null)
        {
            Vms.SetExternalFilter(filter);
        }
    }

    public void NavigateToVmDetail(VmSummary vm)
    {
        var detail = _services.GetRequiredService<VmDetailViewModel>();
        detail.Initialize(vm);
        Current = detail;
        // 详情页不改变 SelectedNav，列表页仍被当成"当前导航项"，
        // 所以必须显式停表，否则在看详情时后台仍在刷列表
        Vms.SetPageActive(false);
    }

    public void NavigateJobs()
    {
        SelectedNav = NavItems.First(n => n.PageKey == "jobs");
        Current = Jobs;
    }

    /// <summary>
    /// 让任务中心重新读一遍 Job 存储。给设置页的「清除本地任务历史」用 ——
    /// 清空写在 <see cref="IJobStore"/> 上，任务页看不到这件事，
    /// 不主动叫它刷新的话，用户切过去还会看到一份已经不存在于磁盘的历史。
    /// </summary>
    public Task RefreshJobsAsync() => Jobs.RefreshAsync();
}
