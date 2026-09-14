using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudFlow.App.ViewModels;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.App.Views;

public partial class VirtualMachinesPage : UserControl
{
    public VirtualMachinesPage()
    {
        InitializeComponent();
    }

    private VirtualMachinesViewModel Vm => (VirtualMachinesViewModel)DataContext;

    /// <summary>
    /// 单击行 → 打开 VM 详情（设计文档 §80 场景入口）。
    /// 复选框与行内“…”菜单等交互控件自身处理点击，不触发导航。
    /// </summary>
    private void VmsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsRowInteractiveControl(source))
        {
            return;
        }

        OpenSelectedVm();
    }

    /// <summary>双击行 → 同样打开 VM 详情（保留既有习惯操作）。</summary>
    private void VmsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedVm();

    /// <summary>每一行各自 FindResource 一次拿一份独立的 ContextMenu 实例——原因见
    /// AllResourcesPage.xaml.cs 同名方法的详细注释。</summary>
    private void VmsGrid_LoadingRow(object sender, DataGridRowEventArgs e) =>
        e.Row.ContextMenu = (ContextMenu)FindResource("Cf.VmRowMenu");

    private void OpenSelectedVm()
    {
        if (VmsGrid.SelectedItem is VmSummary vm)
        {
            Vm.OpenDetailCommand.Execute(vm);
        }
    }

    /// <summary>点击落在 CheckBox / Button（含行内菜单按钮）内时，不视为“打开详情”。</summary>
    private static bool IsRowInteractiveControl(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.CheckBox)
            {
                return true;
            }

            if (node is DataGridRow)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>左键点击「⋯」→ 打开行操作菜单（WPF 默认只响应右键，见 <see cref="RowMenu"/>）。</summary>
    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button)
        {
            RowMenu.OpenFor(button);
        }
    }

    private void MenuViewDetails_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RowMenu.RowDataOf<VmSummary>(sender) is { } vm)
        {
            Vm.OpenDetailCommand.Execute(vm);
        }
    }

    /// <summary>
    /// 行菜单「连接」→ 单台。与详情页右上角那个「连接」共用
    /// <see cref="CloudFlow.App.Infrastructure.SshConnectFlow"/>，行为一致。
    /// </summary>
    private void MenuConnect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RowMenu.RowDataOf<VmSummary>(sender) is { } vm)
        {
            Vm.ConnectCommand.Execute(vm);
        }
    }

    /// <summary>
    /// 行菜单「删除虚拟机」→ 确认框（含连带资源勾选）→ 提交。
    /// <b>一律经审批</b>：DeleteVmHandler 恒返回 CannotBypass，设置里关掉审批档也拦得住。
    /// </summary>
    private void MenuDelete_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RowMenu.RowDataOf<VmSummary>(sender) is { } vm)
        {
            Vm.DeleteCommand.Execute(vm);
        }
    }

    /// <summary>
    /// 表头「本页全选」。走 Click 而不是命令绑定，理由同 XAML 里的注释：
    /// 表头内容的祖先链与数据行不同，从 Header 内部回绑 UserControl 是没有先例的写法。
    /// </summary>
    private void PageSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is VirtualMachinesViewModel vm)
        {
            vm.TogglePageCheckedCommand.Execute(null);
        }
    }

    /// <summary>
    /// 行勾选：把 CheckBox 的状态写回行模型。
    /// </summary>
    /// <remarks>
    /// 不能用 <c>TwoWay</c> 绑定代替：<c>Cf.DataGrid</c> 设了 <c>IsReadOnly="True"</c>，
    /// 只读单元格里 TwoWay 的提交不会发生 —— 复选框视觉上会切换，值却永远不回写，
    /// 表现为"勾了但计数一直是 0"（实测确认，绑定连一次都没触发）。
    /// 这个表格里其它交互控件（<see cref="RowMenu_Click"/>、<see cref="PageSelectAll_Click"/>）
    /// 也都是代码后置处理。
    /// </remarks>
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box && box.DataContext is VmSummary vm)
        {
            vm.IsChecked = box.IsChecked is true;
        }
    }

    private void MenuPower_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var vm = RowMenu.RowDataOf<VmSummary>(sender);
        if (vm is null)
        {
            return;
        }

        var action = (sender as MenuItem)?.Tag as string;
        switch (action)
        {
            case "start":
                Vm.StartCommand.Execute(vm);
                break;
            case "restart":
                Vm.RestartCommand.Execute(vm);
                break;
            case "poweroff":
                Vm.PowerOffCommand.Execute(vm);
                break;
            case "deallocate":
                Vm.DeallocateCommand.Execute(vm);
                break;
        }
    }

    /// <summary>
    /// 菜单打开时按写操作可用性禁用动作项。
    /// ContextMenu 位于独立 Popup，无法用 RelativeSource 绑到页面的 ViewModel，
    /// 因此在这里显式设置 —— 灰显加 ToolTip 说明，好过点了没反应。
    /// </summary>
    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        // 写操作已全部接入 Operation Engine：不再需要按账户状态灰显菜单项。
        // 该钩子保留，作为后续按权限（§31 RBAC）细分动作可用性的落点。
        _ = menu;
    }
}
