using Wpf.Ui.Controls;

namespace CloudFlow.App.ViewModels;

/// <summary>左侧导航条目（概念图 1：Home / Compute ▾ / Jobs / Settings）。</summary>
public sealed class NavItemViewModel
{
    public required string PageKey { get; init; }

    public string Label { get; init; } = "";

    public SymbolRegular Symbol { get; init; } = SymbolRegular.Home24;

    /// <summary>Compute 的子项（Virtual Machines）缩进显示。</summary>
    public bool IsChild { get; init; }

    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;
}
