using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CloudFlow.App.Behaviors;

/// <summary>资源表格右键先选中被点击的行；保留原生 ContextMenu 的打开和目标解析。</summary>
public static class RowSelectionBehavior
{
    public static readonly DependencyProperty SelectOnRightClickProperty = DependencyProperty.RegisterAttached(
        "SelectOnRightClick", typeof(bool), typeof(RowSelectionBehavior), new PropertyMetadata(false, Changed));
    public static void SetSelectOnRightClick(DependencyObject target, bool value) => target.SetValue(SelectOnRightClickProperty, value);
    public static bool GetSelectOnRightClick(DependencyObject target) => (bool)target.GetValue(SelectOnRightClickProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not DataGrid grid) return;
        if ((bool)e.OldValue) grid.PreviewMouseRightButtonDown -= SelectRow;
        if ((bool)e.NewValue) grid.PreviewMouseRightButtonDown += SelectRow;
    }
    private static void SelectRow(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;
        grid.SelectedItem = row.Item;
        // 不标记 Handled：原生菜单仍由被点击行打开，菜单动作仍经过 Operation Engine。
        row.Focus();
    }
}
