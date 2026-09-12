using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.ViewModels;
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
