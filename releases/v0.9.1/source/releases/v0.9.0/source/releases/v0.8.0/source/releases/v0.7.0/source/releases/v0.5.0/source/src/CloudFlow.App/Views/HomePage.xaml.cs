using System.Windows.Controls;
using System.Windows.Input;
using CloudFlow.App.ViewModels;

namespace CloudFlow.App.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    /// <summary>Attention Item 点击 → 跳转对应 VM（有 VmName 时）。</summary>
    private void AttentionItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HomeViewModel vm &&
            (sender as System.Windows.FrameworkElement)?.DataContext is AttentionItem item &&
            item.VmName is not null)
        {
            vm.OpenAttentionItemCommand.Execute(item);
        }
    }
}
