using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CloudFlow.App.Behaviors;

/// <summary>
/// 修复 DataGrid（及其它内建 ScrollViewer 的控件）吞掉鼠标滚轮事件的问题：即使
/// <c>VerticalScrollBarVisibility="Disabled"</c>，控件内部模板里的 ScrollViewer 仍会在
/// PreviewMouseWheel 阶段把事件标记为已处理，导致外层 Tab / 页面级 ScrollViewer 收不到滚轮 ——
/// 表现为"鼠标停在表格上无法滚动整页，必须移到表格外的空白处才能滚"。
///
/// 用法：在共享样式（如 Cf.DataGrid）里加一条 <c>Setter Property="behaviors:ScrollWheelBehavior.BubbleWhenDisabled" Value="True"</c>
/// 即可全局生效——只在该控件自己放弃了垂直滚动条（Disabled）时才代它转发，
/// 有独立滚动条的控件（如某个页面里本来就该自己滚的表格）不受影响，行为不变。
/// </summary>
public static class ScrollWheelBehavior
{
    public static readonly DependencyProperty BubbleWhenDisabledProperty = DependencyProperty.RegisterAttached(
        "BubbleWhenDisabled", typeof(bool), typeof(ScrollWheelBehavior),
        new PropertyMetadata(false, OnBubbleWhenDisabledChanged));

    public static void SetBubbleWhenDisabled(DependencyObject element, bool value) =>
        element.SetValue(BubbleWhenDisabledProperty, value);

    public static bool GetBubbleWhenDisabled(DependencyObject element) =>
        (bool)element.GetValue(BubbleWhenDisabledProperty);

    private static void OnBubbleWhenDisabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source)
        {
            return;
        }

        // 只在这个控件本来就放弃了自己的垂直滚动条时才代它转发——有独立滚动条的控件
        // 必须保留原生行为，不能被这条全局规则误伤。
        if (ScrollViewer.GetVerticalScrollBarVisibility(source) != ScrollBarVisibility.Disabled)
        {
            return;
        }

        var scrollViewer = FindAncestorScrollViewer(source);
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject source)
    {
        var current = VisualTreeHelper.GetParent(source);
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
