using System.Windows;
using System.Windows.Controls;

namespace CloudFlow.App.Controls;

/// <summary>
/// 就地提示条：按 <see cref="SeverityKey"/>（如 "Failed" / "Warning" / "Info"）
/// 走 <c>CfStatusBrushConverter</c> 的统一配色，<see cref="Text"/> 为空时自动隐藏。
/// </summary>
public partial class InlineMessageBanner : UserControl
{
    public static readonly DependencyProperty SeverityKeyProperty = DependencyProperty.Register(
        nameof(SeverityKey), typeof(string), typeof(InlineMessageBanner), new PropertyMetadata(null));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InlineMessageBanner), new PropertyMetadata(null));

    public InlineMessageBanner()
    {
        InitializeComponent();
    }

    public string? SeverityKey
    {
        get => (string?)GetValue(SeverityKeyProperty);
        set => SetValue(SeverityKeyProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
