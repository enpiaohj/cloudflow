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

        NavItems =
        [
            new() { PageKey = "home", Label = "Home", Symbol = SymbolRegular.Home24 },
            new() { PageKey = "compute", Label = "Compute", Symbol = SymbolRegular.Cloud24 },
            new() { PageKey = "vms", Label = "Virtual Machines", Symbol = SymbolRegular.Desktop24, IsChild = true },
            new() { PageKey = "jobs", Label = "Jobs", Symbol = SymbolRegular.Clock24 },
            new() { PageKey = "sep1", IsSeparator = true, IsEnabled = false },
            new() { PageKey = "settings", Label = "Settings", Symbol = SymbolRegular.Settings24 }
        ];

        // Demo 账户显示（真实接入后来自 MSAL 账户缓存）
        AccountOptions = ["Contoso (contoso.onmicrosoft.com)"];
        _selectedAccount = AccountOptions[0];
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private object? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotVmDetailPage))]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    private string _searchText = "";

    public IReadOnlyList<string> AccountOptions { get; }

    [ObservableProperty]
    private string _selectedAccount;

    public IReadOnlyList<string> ScopeOptions { get; private set; } = ["All accessible subscriptions"];

    [ObservableProperty]
    private string _selectedScope = "All accessible subscriptions";

    /// <summary>顶栏账户区副标题（Home 右上角显示）。</summary>
    public string ScopeSubtitle => $"{SelectedAccount.Split(' ')[0]}  |  Scope: {SelectedScope}";

    public bool IsNotVmDetailPage => SelectedNav?.PageKey != "__detail";

    /// <summary>首次加载：Saved Scope → 顶部选项 + 默认 Scope=Production（概念图）。</summary>
    public async Task InitializeAsync()
    {
        var saved = _savedScopeStore.LoadOrDefault();
        _scopeContext.SetSavedScopes(saved);

        var options = new List<string> { "All accessible subscriptions" };
        options.AddRange(saved.Select(s => s.Name));
        ScopeOptions = options;
        OnPropertyChanged(nameof(ScopeOptions));

        var defaultScope = options.FirstOrDefault(o => o == "Production") ?? options[0];
        SelectedScope = defaultScope;
        ApplyScope(defaultScope);

        SelectedNav = NavItems.First(n => n.PageKey == "home");
        if (Current is HomeViewModel home)
        {
            await home.RefreshAsync();
        }
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null || value.IsSeparator)
        {
            return;
        }

        Current = value.PageKey switch
        {
            "home" => Home,
            "vms" or "compute" => Vms,
            "jobs" => Jobs,
            "settings" => Settings,
            _ => Current
        };
    }

    partial void OnSelectedScopeChanged(string value) => ApplyScope(value);

    /// <summary>Scope 贯穿整个应用（设计文档 §11）：设置后各页面订阅 ScopeChanged 自动刷新。</summary>
    private void ApplyScope(string option)
    {
        ResourceScope scope;
        if (option == "All accessible subscriptions")
        {
            scope = new ResourceScope
            {
                ScopeName = option,
                Mode = ScopeMode.AllAccessible
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

    // ==== IShellNavigation ====

    public void NavigateHome()
    {
        SelectedNav = NavItems.First(n => n.PageKey == "home");
    }

    public void NavigateVirtualMachines(string? filter = null)
    {
        if (SelectedNav?.PageKey != "vms")
        {
            SelectedNav = NavItems.First(n => n.PageKey == "vms");
        }
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
