using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.ViewModels;
using CloudFlow.Terminal.Terminal;

namespace CloudFlow.App.Views;

/// <summary>
/// 底部终端面板：终端控件的生命周期所有者。
/// <para>
/// 控件按需创建 —— 只为被激活过的标签建 <see cref="SshTerminalView"/>。
/// 这一点是必须保持的不变量：<c>SshTerminalView</c> 在前端就绪后会自行调用
/// <c>SshSession.ConnectAsync()</c>，因此"没建视图的标签就永远不会连"。
/// </para>
/// </summary>
public partial class TerminalPanel : UserControl
{
    private readonly Dictionary<TerminalTabViewModel, SshTerminalView> _views = [];
    private TerminalPanelViewModel? _panel;

    public TerminalPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookPanel();
    }

    private void HookPanel()
    {
        if (_panel is { } previous)
        {
            previous.Tabs.CollectionChanged -= OnTabsChanged;
            previous.PropertyChanged -= OnPanelPropertyChanged;
        }

        DisposeAllViews();

        _panel = DataContext as TerminalPanelViewModel;
        if (_panel is null)
        {
            return;
        }

        _panel.Tabs.CollectionChanged += OnTabsChanged;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        SyncActiveView();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                foreach (var tab in e.OldItems?.OfType<TerminalTabViewModel>() ?? [])
                {
                    DisposeView(tab);
                }
                break;

            // ShutdownAsync 与任何 Clear 都走这条：一次拆掉全部渲染器
            case NotifyCollectionChangedAction.Reset:
                DisposeAllViews();
                break;
        }

        SyncActiveView();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalPanelViewModel.ActiveTab))
        {
            SyncActiveView();
        }
    }

    /// <summary>确保当前标签有控件，并把其余标签的控件藏起来（不拆，保活）。</summary>
    private void SyncActiveView()
    {
        if (_panel is null)
        {
            return;
        }

        var active = _panel.ActiveTab;
        if (active is not null)
        {
            EnsureView(active);
        }

        foreach (var (tab, view) in _views)
        {
            view.Visibility = ReferenceEquals(tab, active) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsureView(TerminalTabViewModel tab)
    {
        if (_views.ContainsKey(tab))
        {
            return;
        }

        // 日志器必须用面板 VM 的那份：视图初始化失败（WebView2 起不来等）是"待连接挂死"
        // 的头号来源，记进 NullLogger 就永远无迹可寻。
        var view = new SshTerminalView(tab.Session, _panel!.Logger);
        _views[tab] = view;
        TerminalHostArea.Children.Add(view);
    }

    private void DisposeView(TerminalTabViewModel tab)
    {
        if (!_views.Remove(tab, out var view))
        {
            return;
        }

        // 先摘出可视树再释放：让 WebView2 的 HWND 随控件一起消失，
        // 而不是留在一个已经不再布局的容器里
        TerminalHostArea.Children.Remove(view);
        // Dispose 只拆渲染器，不关会话 —— 会话统一由 TerminalPanelViewModel 释放
        view.Dispose();
    }

    private void DisposeAllViews()
    {
        foreach (var view in _views.Values)
        {
            TerminalHostArea.Children.Remove(view);
            view.Dispose();
        }

        _views.Clear();
    }
}
