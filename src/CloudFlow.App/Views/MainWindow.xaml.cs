using System.Windows;
using System.Windows.Input;
using CloudFlow.App.ViewModels;
using Wpf.Ui.Controls;

namespace CloudFlow.App;

/// <summary>
/// 应用主窗口外壳（概念图 1 整体布局）。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"初始化失败：{ex.Message}", "CloudFlow",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>顶栏空白区域拖拽移动窗口（ExtendsContentIntoTitleBar）。</summary>
    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            IsInteractiveElement(source))
        {
            return;
        }
        if (e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标释放后触发的场景，忽略
            }
        }
    }

    private static bool IsInteractiveElement(DependencyObject source)
    {
        // 交互控件（ComboBox / Button / TextBox 等）不触发窗口拖拽
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Control or System.Windows.Controls.TextBox)
            {
                return true;
            }
        }
        return false;
    }
}
