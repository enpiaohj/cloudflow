using System.Windows;
using System.Windows.Controls;

namespace CloudFlow.App.Controls;

/// <summary>
/// 空状态（图标 + 标题 + 说明）。<see cref="Compact"/> 用于首页这类小卡片，缩小图标与间距，
/// 避免空态比卡片本身的内容还"重"。
/// </summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(Wpf.Ui.Controls.SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(Wpf.Ui.Controls.SymbolRegular.Info24));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(EmptyState),
        new PropertyMetadata(false, (d, _) => ((EmptyState)d).ApplyDensity()));

    public EmptyState()
    {
        InitializeComponent();
        ApplyDensity();
    }

    public Wpf.Ui.Controls.SymbolRegular Symbol
    {
        get => (Wpf.Ui.Controls.SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private void ApplyDensity()
    {
        var compact = Compact;
        IconDisc.Width = IconDisc.Height = compact ? 38 : 52;
        IconDisc.CornerRadius = new CornerRadius(compact ? 19 : 26);
        IconGlyph.FontSize = compact ? 18 : 24;
        TitleText.FontSize = compact ? 13 : 14;
        TitleText.Margin = new Thickness(0, compact ? 10 : 14, 0, 0);
        DescriptionText.FontSize = compact ? 12 : 12.5;
    }
}
