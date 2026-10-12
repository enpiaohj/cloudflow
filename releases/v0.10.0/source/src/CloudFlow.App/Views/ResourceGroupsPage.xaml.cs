using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.ViewModels;

namespace CloudFlow.App.Views;

public partial class ResourceGroupsPage : UserControl
{
    public ResourceGroupsPage()
    {
        InitializeComponent();
    }

    private ResourceGroupsViewModel Vm => (ResourceGroupsViewModel)DataContext;

    /// <summary>每一行各自 FindResource 一次拿一份独立的 ContextMenu 实例——原因见
    /// AllResourcesPage.xaml.cs 同名方法的详细注释。</summary>
    private void ResourceGroupsGrid_LoadingRow(object sender, DataGridRowEventArgs e) =>
        e.Row.ContextMenu = (ContextMenu)FindResource("Cf.ResourceGroupRowMenu");

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button)
        {
            RowMenu.OpenFor(button);
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowMenu.RowDataOf<ResourceGroupRow>(sender) is { } row)
        {
            Vm.DeleteResourceGroupCommand.Execute(row);
        }
    }

    /// <summary>
    /// 行勾选：把 CheckBox 的状态写回行模型。不能用 TwoWay 代替——<c>Cf.DataGrid</c> 只读，
    /// 只读单元格里 TwoWay 的提交不会发生（与虚拟机列表页的 <c>RowCheck_Click</c> 同一个坑）。
    /// </summary>
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is ResourceGroupRow row)
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
