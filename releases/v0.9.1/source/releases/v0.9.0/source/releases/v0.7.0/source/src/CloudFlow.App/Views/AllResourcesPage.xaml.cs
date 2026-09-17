using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.ViewModels;

namespace CloudFlow.App.Views;

public partial class AllResourcesPage : UserControl
{
    public AllResourcesPage()
    {
        InitializeComponent();
    }

    private AllResourcesViewModel Vm => (AllResourcesViewModel)DataContext;

    /// <summary>
    /// 每一行各自 FindResource 一次拿一份独立的 ContextMenu 实例——不能用
    /// DataGrid.RowStyle 的 Setter（所有行共用同一个 Style 对象，Setter 的 StaticResource
    /// 只解析一次，会把同一个 ContextMenu 实例分给所有行，行不虚拟化时几乎同时生成的多行
    /// 会一起抢这份实例的逻辑父级归属，真实复现过：第一条记录右键第一次没反应，右键别的
    /// 行之后归属权"稳定"下来，回头再右键第一条又恢复正常）。这里的 FindResource 命中的是
    /// 标了 x:Shared="False" 的资源，每次都是全新实例，跟"⋯"按钮那条路径（每行的
    /// DataTemplate 各自实例化一次）原理一致。
    /// </summary>
    private void AllResourcesGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        // 虚拟机行没有删除入口——同一条理由已经用在"⋯"按钮上（虚拟机有自己专门的删除
        // 流程，这里只是第二道防线），整行右键也不能绕过去。
        e.Row.ContextMenu = e.Row.DataContext is AllResourceRow { IsVirtualMachine: true }
            ? null
            : (ContextMenu)FindResource("Cf.AllResourcesRowMenu");
    }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button)
        {
            RowMenu.OpenFor(button);
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowMenu.RowDataOf<AllResourceRow>(sender) is { } row)
        {
            Vm.DeleteResourceCommand.Execute(row);
        }
    }

    /// <summary>
    /// 行勾选：把 CheckBox 的状态写回行模型。不能用 TwoWay 代替——<c>Cf.DataGrid</c> 只读，
    /// 只读单元格里 TwoWay 的提交不会发生（与虚拟机列表页的 <c>RowCheck_Click</c> 同一个坑）。
    /// </summary>
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is AllResourceRow row)
        {
            row.IsChecked = box.IsChecked is true;
        }
    }

    /// <summary>表头全选：走 Click 而不是命令绑定，理由同虚拟机列表页的 <c>PageSelectAll_Click</c>。</summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Vm.ToggleAllCheckedCommand.Execute(null);
    }
}
