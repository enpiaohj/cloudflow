using System.Windows.Controls;

namespace CloudFlow.App.Views;

public partial class JobsPage : UserControl
{
    public JobsPage()
    {
        InitializeComponent();
    }
    private void JobDetails_Click(object sender, System.Windows.RoutedEventArgs e) =>
        Behaviors.JobDetailsBehavior.DetailsClick(sender, e);
}
