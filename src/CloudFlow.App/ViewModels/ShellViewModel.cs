using Wpf.Ui.Controls;
using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Scopes;
using CloudFlow.Data.Stores;
using CloudFlow.Modules.Compute.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 应用外壳：左侧导航 + 顶栏（Account / Scope / 全局搜索）。
/// 对应概念图 1 顶栏与左栏；Scope 命名遵循设计文档 §8（不用 "Subscription"）。
/// </summary>
public partial class ShellViewModel : ObservableObject, IShellNavigation
{
    private readonly IServiceProvider _services;
    private readonly ScopeContext _scopeContext;
    private readonly SavedScopeStore _savedScopeStore;

    private HomeViewModel Home => _services.GetRequiredService<HomeViewModel>();
    private VirtualMachinesViewModel Vms => _services.GetRequiredService<VirtualMachinesViewModel>();
    private JobsViewModel Jobs => _services.GetRequiredService<JobsViewModel>();
    private SettingsViewModel Settings => _services.GetRequiredService<SettingsViewModel>();

    public ShellViewModel(
        IServiceProvider services,
        ScopeContext scopeContext,
        SavedScopeStore savedScopeStore)
    {
        _services = services;
        _scopeContext = scopeContext;
        _savedScopeStore = savedScopeStore;

        // "计算"为可展开分组，"虚拟机"子项默认折叠
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

        // Demo 账户显示（真实接入后来自 MSAL 账户缓存）
        AccountOptions = ["Contoso (contoso.onmicrosoft.com)"];
        _selectedAccount = AccountOptions[0];
        _selectedScope = "全部可访问订阅";
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    /// <summary>顶栏账户列表（登录后替换为真实账户）。</summary>
    public ObservableCollection<string> AccountOptions { get; private set; } = ["Contoso (contoso.onmicrosoft.com)"];

    [ObservableProperty]
    private object? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotVmDetailPage))]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedAccount;

    public IReadOnlyList<string> ScopeOptions { get; private set; } = ["全部可访问订阅"];

    [ObservableProperty]
    private string _selectedScope = "全部可访问订阅";

    /// <summary>顶栏账户区副标题（Home 右上角显示）。</summary>
    public string ScopeSubtitle => $"{SelectedAccount.Split(' ')[0]}  |  范围：{SelectedScope}";

    public bool IsNotVmDetailPage => SelectedNav?.PageKey != "__detail";

    /// <summary>首次加载：Saved Scope → 顶部选项 + 默认 Scope=Production（概念图）。</summary>
    public async Task InitializeAsync()
    {
        var saved = _savedScopeStore.LoadOrDefault();
        _scopeContext.SetSavedScopes(saved);
        BuildScopeOptions();

        var defaultScope = "全部可访问订阅";
        if (_scopeContext.AvailableSubscriptions.Count == 0)
        {
            defaultScope = ScopeOptions.FirstOrDefault(o => o == "Production") ?? ScopeOptions[0];
        }
        SelectedScope = defaultScope;
        ApplyScope(defaultScope);

        SelectedNav = NavItems.First(n => n.PageKey == "home");
        if (Current is HomeViewModel home)
        {
            await home.RefreshAsync();
        }
    }

    private NavItemViewModel? _lastSelectedNav;
    private bool _suppressNavSelection;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (_suppressNavSelection || value is null || value.IsSeparator)
        {
            return;
        }

        // 分组头（如"计算"）：点击仅切换展开/折叠，不导航；选中态恢复到之前的项
        if (value.IsGroup)
        {
            value.IsExpanded = !value.IsExpanded;
            _suppressNavSelection = true;
            SelectedNav = _lastSelectedNav;
            _suppressNavSelection = false;
            return;
        }

        _lastSelectedNav = value;
        switch (value.PageKey)
        {
            case "home":
                Current = Home;
                break;

            case "vms":
                Current = Vms;
                // 进入页面即刷新（页面为懒加载单例，避免首次进入时空列表）
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

    partial void OnSelectedScopeChanged(string value) => ApplyScope(value);

    /// <summary>Scope 贯穿整个应用（设计文档 §11）：设置后各页面订阅 ScopeChanged 自动刷新。</summary>
    private void ApplyScope(string option)
    {
        ResourceScope scope;
        var realSub = _scopeContext.AvailableSubscriptions.FirstOrDefault(
            s => string.Equals(s.DisplayName, option, StringComparison.OrdinalIgnoreCase));

        if (option == "全部可访问订阅")
        {
            scope = new ResourceScope
            {
                ScopeName = option,
                Mode = ScopeMode.AllAccessible
            };
        }
        else if (realSub is not null)
        {
            // 登录后的真实订阅：单订阅 Scope
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

    [RelayCommand]
    private Task SearchAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            NavigateVirtualMachines(SearchText.Trim());
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        return Current switch
        {
            HomeViewModel home => home.RefreshAsync(),
            VirtualMachinesViewModel vms => vms.RefreshAsync(),
            JobsViewModel jobs => jobs.RefreshAsync(),
            _ => Task.CompletedTask
        };
    }

    /// <summary>
    /// 登录成功后调用：顶栏账户、Scope 选项切换为真实订阅（设计文档 §11 Scope 贯穿全应用）。
    /// </summary>
    public void OnSignedIn(
        CloudFlow.Core.Identity.CloudAccount account,
        IReadOnlyList<CloudFlow.Core.Identity.SubscriptionProfile> subscriptions)
    {
        AccountOptions = [$"{account.DisplayName} ({account.Username})"];
        OnPropertyChanged(nameof(AccountOptions));
        SelectedAccount = AccountOptions[0];

        _scopeContext.SetAvailableSubscriptions(subscriptions);
        BuildScopeOptions();

        // 登录后默认回到"全部可访问订阅"，触发全页面刷新
        SelectedScope = "全部可访问订阅";
        ApplyScope(SelectedScope);
    }

    /// <summary>Scope 选项：登录后为真实订阅名；未登录为 Saved Scopes（Demo）。</summary>
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

    // ==== IShellNavigation ====

    public void NavigateHome()
    {
        SelectedNav = NavItems.First(n => n.PageKey == "home");
    }

    public void NavigateVirtualMachines(string? filter = null)
    {
        // 确保分组展开（从 Quick Actions / 注意项 / 搜索进入时）
        NavItems.First(n => n.PageKey == "compute").IsExpanded = true;

        var vmsItem = NavItems.First(n => n.PageKey == "vms");
        if (SelectedNav != vmsItem)
        {
            SelectedNav = vmsItem;
        }
        _lastSelectedNav = vmsItem;
        Current = Vms;
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
    }

    public void NavigateJobs()
    {
        SelectedNav = NavItems.First(n => n.PageKey == "jobs");
        Current = Jobs;
    }
}
