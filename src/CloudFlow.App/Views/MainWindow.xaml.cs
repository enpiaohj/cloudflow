using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
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

    /// <summary>退出收尾只走一次（收尾完成后会主动再关一次窗口）。</summary>
    private bool _shutdownStarted;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
        AccountMenuPopup.CustomPopupPlacementCallback = PlaceAccountMenu;
        Loaded += OnLoaded;
    }

    /// <summary>拖动终端面板抓手：把位移交给面板 VM，由它统一钳制高度。</summary>
    private void PanelGrip_DragDelta(object sender, DragDeltaEventArgs e) =>
        _shell.Terminal.ResizeBy(e.VerticalChange, BodyGrid.ActualHeight);

    /// <summary>窗口变矮后重新钳制面板高度，否则面板会把页面挤没、底边还会跑出窗口。</summary>
    private void BodyGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        _shell.Terminal.ClampToBody(e.NewSize.Height);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // 没有会话就没什么要收尾的，直接关。
        // 收尾必须在 UI 线程上 await（SshSession 的清理会回到捕获的 SynchronizationContext），
        // 在这里同步等待就是死锁 —— 所以取消本次关闭，收尾完成后再主动关一次。
        if (_shutdownStarted || _shell.Terminal.Tabs.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        _shutdownStarted = true;
        _ = ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            await _shell.Terminal.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 超时或个别会话释放失败都不该挡住退出：
            // 进程结束时由 OS 关掉残留 socket（Windows 注销不走 Closing，本就走这条兜底）
        }

        Close();
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

    /// <summary>顶栏账户按钮：打开账户菜单（切换账户 / 添加账户 / 管理账户）。</summary>
    private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
    {
        // 必须等本次点击（鼠标抬起）处理完毕再打开：
        // 在 Click 处理器内同步打开，会被同一次鼠标抬起立即关闭，菜单表现为“点了没反应”。
        Dispatcher.BeginInvoke(
            new Action(() => AccountMenuPopup.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// 账户菜单右对齐到账户按钮：账户控件位于顶栏右侧，左对齐会让菜单探出窗口右边缘。
    /// 尺寸由 Popup 布局引擎提供（避免自行测量时的时序误差）。
    /// </summary>
    private static CustomPopupPlacement[] PlaceAccountMenu(Size popupSize, Size targetSize, Point offset) =>
    [
        // popupSize 不含子元素 Margin（卡片四周各有 10px 阴影留白），
        // 因此横向再多减 10px：卡片右边缘与按钮右边缘对齐；纵向留 8px 间距。
        new CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width - 10, targetSize.Height - 2),
            PopupPrimaryAxis.Horizontal)
    ];

    /// <summary>账户菜单项执行后立即收起菜单，避免在点击处留下悬空菜单。</summary>
    private void AccountMenuAction_Click(object sender, RoutedEventArgs e) => AccountMenuPopup.IsOpen = false;

    /// <summary>顶栏"任务进行中"徽标：打开正在跑的任务列表（同一个"等鼠标抬起再开"的理由）。</summary>
    private void RunningJobsButton_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(() => RunningJobsPopup.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>"查看全部任务"点击后收起弹层，避免留下悬空菜单。</summary>
    private void RunningJobsMenuAction_Click(object sender, RoutedEventArgs e) => RunningJobsPopup.IsOpen = false;

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
