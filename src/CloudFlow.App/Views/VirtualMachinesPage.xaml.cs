using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>双击行 → 打开 VM 详情（概念图 2 场景入口）。</summary>
    private void VmsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VmsGrid.SelectedItem is VmSummary vm)
        {
            Vm.OpenDetailCommand.Execute(vm);
        }
    }

    private void MenuViewDetails_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (GetRowVm(sender) is { } vm)
        {
            Vm.OpenDetailCommand.Execute(vm);
        }
    }

    private void MenuPower_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var vm = GetRowVm(sender);
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

    /// <summary>MenuItem → ContextMenu → PlacementTarget(Button).Tag 取行数据。</summary>
    private static VmSummary? GetRowVm(object sender)
    {
        if (sender is not FrameworkElement menuItem)
        {
            return null;
        }
        var menu = menuItem.Parent as ContextMenu ?? menuItem.TemplatedParent as ContextMenu;
        return (menu?.PlacementTarget as FrameworkElement)?.Tag as VmSummary;
    }
}
