using System.ComponentModel;
using CloudFlow.Core.Identity;

namespace CloudFlow.Core.Scopes;

/// <summary>
/// 全局 Scope 上下文：当前活动 Account + 当前 ResourceScope + Saved Scopes。
/// 整个应用（Home / Compute / Cost / AI / Automation）共享同一上下文（设计文档 §11）。
/// 切换 Scope 时通过 <see cref="ScopeChanged"/> 通知所有模块刷新。
/// </summary>
public sealed class ScopeContext : INotifyPropertyChanged
{
    private CloudAccount? _activeAccount;
    private ResourceScope _currentScope = new() { Mode = ScopeMode.AllAccessible, ScopeName = "All accessible subscriptions" };
    private List<SavedScope> _savedScopes = [];

    /// <summary>当前活动账户（P1 允许单活动账户，§75）。</summary>
    public CloudAccount? ActiveAccount
    {
        get => _activeAccount;
        private set
        {
            if (ReferenceEquals(_activeAccount, value))
            {
                return;
            }
            _activeAccount = value;
            OnPropertyChanged(nameof(ActiveAccount));
        }
    }

    /// <summary>当前生效的 ResourceScope。</summary>
    public ResourceScope CurrentScope
    {
        get => _currentScope;
        private set
        {
            _currentScope = value;
            OnPropertyChanged(nameof(CurrentScope));
        }
    }

    public IReadOnlyList<SavedScope> SavedScopes
    {
        get => _savedScopes;
        private set
        {
            _savedScopes = [.. value];
            OnPropertyChanged(nameof(SavedScopes));
        }
    }

    private IReadOnlyList<Identity.SubscriptionProfile> _availableSubscriptions =
        [];

    /// <summary>
    /// 登录后发现的真实订阅列表（Shell 据此重建 Scope 选项）。
    /// 未登录时为空，此时使用 SavedScopes（Demo）。
    /// </summary>
    public IReadOnlyList<Identity.SubscriptionProfile> AvailableSubscriptions
    {
        get => _availableSubscriptions;
        private set
        {
            _availableSubscriptions = value;
            OnPropertyChanged(nameof(AvailableSubscriptions));
        }
    }

    /// <summary>登录后写入真实订阅。</summary>
    public void SetAvailableSubscriptions(IEnumerable<Identity.SubscriptionProfile> subs)
    {
        AvailableSubscriptions = [.. subs];
    }

    /// <summary>Scope / Account 变更事件（模块订阅后刷新数据）。</summary>
    public event EventHandler? ScopeChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetActiveAccount(CloudAccount? account)
    {
        ActiveAccount = account;
    }

    public void SetScope(ResourceScope scope)
    {
        CurrentScope = scope;
        OnScopeChanged();
    }

    public void SetSavedScopes(IEnumerable<SavedScope> scopes)
    {
        SavedScopes = [.. scopes];
    }

    private void OnScopeChanged() => ScopeChanged?.Invoke(this, EventArgs.Empty);

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
