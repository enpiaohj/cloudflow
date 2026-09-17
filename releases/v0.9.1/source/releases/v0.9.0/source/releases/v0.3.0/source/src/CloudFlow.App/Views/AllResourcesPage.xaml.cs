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
}
