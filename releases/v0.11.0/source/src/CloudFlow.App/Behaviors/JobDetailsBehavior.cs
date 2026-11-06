using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CloudFlow.App.Views;
using CloudFlow.App.ViewModels;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using Microsoft.Extensions.DependencyInjection;

namespace CloudFlow.App.Behaviors;

public static class JobDetailsBehavior
{
    private static readonly HashSet<Window> OpenOwners = [];
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(JobDetailsBehavior), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject target,bool value)=>target.SetValue(EnabledProperty,value);
    public static bool GetEnabled(DependencyObject target)=>(bool)target.GetValue(EnabledProperty);
    private static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs e)
    {
        if(target is not DataGrid grid) return;
        if((bool)e.OldValue) { grid.MouseDoubleClick-=DoubleClick; grid.PreviewKeyDown-=KeyDown; }
        if((bool)e.NewValue) { grid.MouseDoubleClick+=DoubleClick; grid.PreviewKeyDown+=KeyDown; }
    }
    public static OperationJob? JobOf(object? row) => row switch { OperationJob job=>job, JobRow rowJob=>rowJob.Job, RunningJobRow running=>running.Job, _=>null };
    public static async Task OpenAsync(object? row,Window? owner = null)
    {
        if(JobOf(row) is not { } job) return;
        owner ??= Application.Current?.MainWindow;
        if(owner is not null && !OpenOwners.Add(owner)) return;
        try
        {
            var services = App.Services;
            var dialog=new JobDetailsDialog(job,services?.GetService<IJobStore>(),services?.GetService<IAuditLog>(),services?.GetService<ScopeContext>()) { Owner=owner };
            await dialog.ShowPanelAsync();
        }
        finally { if(owner is not null) OpenOwners.Remove(owner); }
    }
    private static async void DoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || e.OriginalSource is not DependencyObject source || sender is not DataGrid grid) return;
        if(ItemsControl.ContainerFromElement(grid,source) is not DataGridRow row || FromButton(source)) return;
        e.Handled=true; await OpenAsync(row.Item,Window.GetWindow(grid));
    }
    private static async void KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter || sender is not DataGrid grid || e.OriginalSource is not DependencyObject source || FromButton(source)) return;
        if(JobOf(grid.SelectedItem) is null) return;
        e.Handled=true; await OpenAsync(grid.SelectedItem,Window.GetWindow(grid));
    }
    private static bool FromButton(DependencyObject source)
    {
        for(var node=source;node is not null;node=ParentOf(node))
        { if(node is ButtonBase) return true; if(node is DataGridRow) break; }
        return false;
    }
    private static DependencyObject? ParentOf(DependencyObject node) => node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
        ? System.Windows.Media.VisualTreeHelper.GetParent(node)
        : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
    public static async void DetailsClick(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement element) { e.Handled=true; await OpenAsync(element.DataContext,Window.GetWindow(element)); }
    }
}
