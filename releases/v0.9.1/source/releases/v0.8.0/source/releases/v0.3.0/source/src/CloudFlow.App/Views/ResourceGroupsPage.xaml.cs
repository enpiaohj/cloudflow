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
}
