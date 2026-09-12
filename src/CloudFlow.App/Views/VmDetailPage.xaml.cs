using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.ViewModels;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.App.Views;

public partial class VmDetailPage : UserControl
{
    public VmDetailPage()
    {
        InitializeComponent();
    }

    private VmDetailViewModel Vm => (VmDetailViewModel)DataContext;

    private void MenuDeallocate_Click(object sender, RoutedEventArgs e)
    {
        Vm.DeallocateFromMenu();
    }

    private async void MenuStart_Click(object sender, RoutedEventArgs e)
    {
        await Vm.StartCommand.ExecuteAsync(null);
    }

    private async void MenuResize_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ResizeDialog(Vm.VmSize, Vm.ResizeSizeOptions)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() is true && !string.IsNullOrEmpty(dialog.NewSize))
        {
            await Vm.ResizeFromMenuAsync(dialog.NewSize);
        }
    }

    private async void MenuSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var menu = (sender as FrameworkElement)?.Parent as ContextMenu;
        if ((menu?.PlacementTarget as FrameworkElement)?.Tag is VmDiskInfo disk)
        {
            await Vm.CreateSnapshotCommand.ExecuteAsync(disk);
        }
    }

    private void MenuCopyResourceId_Click(object sender, RoutedEventArgs e)
    {
        // Resource ID 复制（设计文档 §30：统一主键常用于跨系统引用）
        if (!string.IsNullOrEmpty(Vm.ResourceId))
        {
            Clipboard.SetText(Vm.ResourceId);
        }
    }

    private void MenuChangePort_Click(object sender, RoutedEventArgs e)
    {
        if (GetRule(sender) is { } rule)
        {
            Vm.ChangePortCommand.Execute(rule);
        }
    }

    private void MenuDeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (GetRule(sender) is { } rule)
        {
            Vm.DeleteRuleCommand.Execute(rule);
        }
    }

    private static NsgSecurityRule? GetRule(object sender)
    {
        if (sender is not FrameworkElement menuItem)
        {
            return null;
        }
        var menu = menuItem.Parent as ContextMenu ?? menuItem.TemplatedParent as ContextMenu;
        return (menu?.PlacementTarget as FrameworkElement)?.Tag as NsgSecurityRule;
    }
}
