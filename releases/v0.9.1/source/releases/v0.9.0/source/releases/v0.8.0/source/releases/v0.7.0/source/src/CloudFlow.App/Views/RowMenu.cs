using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CloudFlow.App.Views;

/// <summary>
/// 行操作菜单的共用行为。
///
/// 一份菜单有两个入口：「⋯」按钮（左键）与整行右键。两者都把
/// <see cref="ContextMenu.PlacementTarget"/> 指向被点的元素，
/// 行数据因此可以统一取到 —— 见 <see cref="RowDataOf{T}"/>。
/// </summary>
internal static class RowMenu
{
    /// <summary>
    /// 打开按钮自身的 ContextMenu，菜单显示在按钮正下方。
    /// WPF 的 <see cref="ContextMenu"/> 默认只响应右键，按钮若不处理 Click，
    /// 左键点上去毫无反应 —— 而用户看到「⋯」的第一反应就是左键。
    /// </summary>
    public static void OpenFor(FrameworkElement button)
    {
        if (button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 取菜单作用的那一行数据。
    ///
    /// 两条入口的存放位置不同：「⋯」按钮把行数据放在自身的 <c>Tag</c>；
    /// 行右键时 PlacementTarget 是 DataGridRow，行数据在它的 DataContext。
    /// 先 Tag 后 DataContext，两种入口共用同一份菜单与同一批回调。
    /// </summary>
    public static T? RowDataOf<T>(object sender) where T : class
    {
        if (MenuOf(sender)?.PlacementTarget is not FrameworkElement target)
        {
            return null;
        }

        return target.Tag as T ?? target.DataContext as T;
    }

    /// <summary>MenuItem → 它所属的 ContextMenu（逻辑父级或模板父级）。</summary>
    public static ContextMenu? MenuOf(object sender) =>
        sender is FrameworkElement item
            ? item.Parent as ContextMenu ?? item.TemplatedParent as ContextMenu
            : null;
}
