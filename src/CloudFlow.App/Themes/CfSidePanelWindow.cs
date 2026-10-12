using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;

namespace CloudFlow.App.Themes;

/// <summary>贴合主窗口右侧的审批编辑面板，保持拥有窗口的上下文可见。</summary>
public class CfSidePanelWindow : CfDialogWindow
{
    private static readonly ConditionalWeakTable<Window, CfSidePanelWindow> ActivePanels = new();
    private TaskCompletionSource<bool?>? _panelCompletion;
    private bool? _panelResult;

    /// <summary>将编辑内容放入拥有窗口，保持原有关闭校验和审批生命周期。</summary>
    public Task<bool?> ShowPanelAsync()
    {
        if (Owner is not { IsVisible: true, Content: Grid root } owner || Content is not FrameworkElement content)
            return Task.FromResult(ShowDialog());
        if (ActivePanels.TryGetValue(owner, out _))
            throw new InvalidOperationException("请先完成或关闭当前编辑面板。");

        _panelCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActivePanels.Add(owner, this);
        var previousFocus = Keyboard.FocusedElement;
        Content = null;
        content.DataContext = DataContext;
        var surface = new Border { Child = content, Width = Math.Min(460, Math.Max(280, root.ActualWidth - 24)),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var overlay = new Grid { Focusable = true };
        overlay.SetResourceReference(Panel.BackgroundProperty, "Cf.OverlayBrush");
        overlay.Children.Add(surface);
        Panel.SetZIndex(overlay, 1000);
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(overlay, KeyboardNavigationMode.Contained);
        overlay.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
        void OwnerClosing(object? sender, CancelEventArgs args)
        {
            // 先关闭编辑面板，交由它的 Closing 处理未完成审批和执行中保护。
            args.Cancel = true;
            Close();
        }
        void Cleanup(object? sender, EventArgs args)
        {
            Closed -= Cleanup;
            owner.Closing -= OwnerClosing;
            owner.Closed -= Cleanup;
            root.Children.Remove(overlay);
            surface.Child = null;
            ActivePanels.Remove(owner);
            _panelCompletion.TrySetResult(_panelResult);
            if (owner.IsVisible && previousFocus is UIElement { IsVisible: true } element) element.Focus();
        }
        Closed += Cleanup;
        owner.Closing += OwnerClosing;
        owner.Closed += Cleanup;
        root.Children.Add(overlay);
        overlay.Focus();
        return _panelCompletion.Task;
    }

    protected void CompletePanel(bool result)
    {
        if (_panelCompletion is null) DialogResult = result;
        else { _panelResult = result; Close(); }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        if (Owner is null || !Owner.IsVisible)
        {
            base.OnContentRendered(e);
            return;
        }
        // WPF 在 SourceInitialized 后仍会应用初始坐标；此时定位才不会被覆盖。
        // 最大化窗口的 Left/Top 是还原坐标，使用实际屏幕位置并换算为 DIP。
        var origin = Owner.PointToScreen(new Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Owner);
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(Owner).Handle).WorkingArea;
        var work = new Rect(screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY,
            screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
        Width = Math.Min(work.Width, Math.Min(460, Math.Max(380, Owner.ActualWidth * .38)));
        Height = Math.Min(work.Height, Math.Max(320, Owner.ActualHeight - 48));
        Left = Math.Clamp(origin.X / dpi.DpiScaleX + Owner.ActualWidth - Width - 16, work.Left, work.Right - Width);
        Top = Math.Clamp(origin.Y / dpi.DpiScaleY + 24, work.Top, work.Bottom - Height);
        Activate();
        base.OnContentRendered(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
