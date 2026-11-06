using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 左侧导航条目（概念图 1：Home / Compute ▾ / Jobs / Settings）。
/// IsGroup 项（如"计算"）为可展开分组，点击切换 IsExpanded；子项默认折叠。
/// </summary>
public sealed class NavItemViewModel : ObservableObject
{
    public required string PageKey { get; init; }

    public string Label { get; init; } = "";

    public SymbolRegular Symbol { get; init; } = SymbolRegular.Home24;

    /// <summary>分组下的子项（如"虚拟机"），随父项展开显示并缩进。</summary>
    public bool IsChild { get; init; }

    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>是否为可展开分组。</summary>
    public bool IsGroup { get; init; }

    private bool _isExpanded;

    /// <summary>分组展开状态（默认折叠）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    /// <summary>父分组引用（子项可见性随 Parent.IsExpanded 变化）。</summary>
    public NavItemViewModel? Parent { get; init; }
}
