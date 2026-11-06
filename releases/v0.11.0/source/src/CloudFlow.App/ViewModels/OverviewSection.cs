namespace CloudFlow.App.ViewModels;

/// <summary>概览页的一行：标签 + 值（+ 可选复制按钮 / tooltip）。</summary>
public sealed class OverviewRow
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    /// <summary>非空时该行右侧显示复制按钮，点它复制这个值（而不是显示文本）。</summary>
    public string? CopyValue { get; init; }

    public string? ToolTip { get; init; }

    public bool CanCopy => !string.IsNullOrEmpty(CopyValue);
}

/// <summary>
/// 概览页的一个分组。
///
/// 用数据驱动而不是在 XAML 里堆固定行：**空行在构造期就被丢掉，空分组整组不出现在列表里**，
/// 所以"Azure 没返回的字段不显示"是数据结构保证的，不靠几十个 Visibility 绑定去维持 ——
/// 那种写法漏一个就会渲染出一行 "—"，而 "—" 与"这个字段根本没有"在界面上分不出来。
/// </summary>
public sealed class OverviewSection
{
    public required string Title { get; init; }

    public required IReadOnlyList<OverviewRow> Rows { get; init; }
}
