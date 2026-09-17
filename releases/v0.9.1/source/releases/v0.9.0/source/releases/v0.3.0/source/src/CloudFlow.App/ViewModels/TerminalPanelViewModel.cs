using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Terminal.Ssh;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 主窗口底部终端面板（VS Code 风格）的会话聚合。
/// <para>
/// <b>它是 SSH 会话的唯一所有者。</b> 会话原先挂在 <see cref="VmDetailViewModel"/> 上，
/// 而那是 <c>AddTransient</c> 注册 —— 每次进详情页都是一个新实例，旧实例的清理永远够不到
/// 上一条会话，会话会一直活到进程退出。移到应用级单例后，跨页面存活与释放都只有一个归属。
/// </para>
/// <para>纯状态对象，不持有任何 <c>UIElement</c>；终端控件由 <c>TerminalPanel.xaml.cs</c> 按需创建。</para>
/// </summary>
public sealed partial class TerminalPanelViewModel : ObservableObject
{
    /// <summary>
    /// 面板的<b>整体</b>最小高度（含标签条那 36px）：再矮终端只剩两三行，没有意义。
    /// </summary>
    public const double MinPanelHeight = 140;

    /// <summary>页面区必须保留的高度 —— 面板拖得再高也不能把页面挤没。</summary>
    public const double PageMinHeight = 160;

    /// <summary>抓手（Thumb）的高度：它在面板之外单占一行，最大化时要扣掉，否则底边被裁。</summary>
    private const double GripHeight = 4;

    private const double DefaultPanelHeight = 316;   // 36 标签条 + 280 内容

    private readonly ILogger<TerminalPanelViewModel> _logger;

    public TerminalPanelViewModel(ILogger<TerminalPanelViewModel>? logger = null)
    {
        _logger = logger ?? NullLogger<TerminalPanelViewModel>.Instance;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    /// <summary>是否有会话。顶栏入口按钮据此显示/隐藏 —— 没有会话时按钮一并消失。</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>顶栏按钮的悬停提示：逐个列出会话，便于在收起状态下确认还剩哪些连接。</summary>
    public string TabsTip => Tabs.Count == 0
        ? "终端会话"
        : $"终端会话（{Tabs.Count}）：\n" + string.Join("\n", Tabs.Select(t => $"{t.VmName} — {t.Target}"));

    [ObservableProperty]
    private TerminalTabViewModel? _activeTab;

    /// <summary>面板是否展开。收起 = 面板整体 Collapsed，底部不留任何残留。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// 面板整体高度（含标签条）。行高保持 Auto，由它作为唯一写者驱动 —— 抓手只改这个值。
    /// </summary>
    /// <remarks>
    /// 它曾经只表示"内容区高度"，标签条那 36px 不在坐标系里：于是"页面至少留 160px"
    /// 实际只留了 124px，最大化时更会整整溢出 36px。改成整体高度后，栅格里两个 Auto 行的
    /// 总高正好是 <c>PanelHeight + 抓手</c>，钳制算一次就对。绑定在 TerminalPanel.xaml 的 Border 上。
    /// </remarks>
    [ObservableProperty]
    private double _panelHeight = DefaultPanelHeight;

    /// <summary>面板是否已最大化（占满主体区、页面区让位）。拖动抓手会自动退出。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximizeTip))]
    private bool _isMaximized;

    /// <summary>
    /// 面板是否已全屏（连同顶栏与左导航一起让位，铺满整个窗口）。
    /// 与 <see cref="IsMaximized"/> 互斥：两者都表示"填满"，同时打开会让两个按钮同时显示
    /// "还原"，用户看不出该点哪个；命令里靠"只在未填满时才置位"维持这个不变式。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenTip))]
    private bool _isFullScreen;

    /// <summary>最大化按钮的悬停提示（同一个按钮兼作还原）。</summary>
    public string MaximizeTip => IsMaximized ? "还原面板高度" : "最大化面板";

    /// <summary>全屏按钮的悬停提示（同一个按钮兼作还原）。</summary>
    public string FullScreenTip => IsFullScreen ? "退出全屏" : "全屏（顶栏与导航一起让位）";

    /// <summary>面板是否正在填满可用高度 —— 最大化与全屏都算，两者的钳制与还原行为一致。</summary>
    private bool FillsBody => IsMaximized || IsFullScreen;

    /// <summary>进入最大化/全屏之前的高度，用于还原。</summary>
    private double _restoreHeight = DefaultPanelHeight;

    /// <summary>主体区高度，由 <see cref="ClampToBody"/> 记住 —— 填满时要拿它算满高。</summary>
    private double _bodyHeight;

    /// <summary>
    /// 打开该 VM 的会话标签，已有则<b>复用标签并替换会话</b>（用户已定：一台 VM 一个标签）。
    /// </summary>
    public void OpenOrActivate(VmSummary vm, string host, SshConnectionOptions options) =>
        OpenOrActivate(vm.ResourceId, vm.Name, host, options);

    /// <summary>
    /// 同上，但以<b>主键与显示名</b>标识目标。
    /// </summary>
    /// <remarks>
    /// 批量连接的目标是 <see cref="CloudFlow.Terminal.Ssh.SshBatchTarget"/> —— 一个刻意不认识
    /// <c>VmSummary</c> 的中性类型（那会构成 Terminal → Modules.Compute 的层次倒挂）。
    /// 而本方法真正用到的只有主键和显示名两个字段，没必要为了类型好看去造一个假的 VmSummary。
    /// </remarks>
    public void OpenOrActivate(string vmResourceId, string vmName, string host, SshConnectionOptions options)
    {
        // 必须在创建会话/激活标签之前展开：面板收起时内容区高度为 0，
        // 终端控件会在 0 尺寸下完成首次布局与 fit，把默认的 80×24 推给远端。
        IsExpanded = true;

        var session = new SshSession(options, NullLogger<SshSession>.Instance);
        var tab = new TerminalTabViewModel(vmResourceId, vmName, host, session);

        var index = IndexOfTab(vmResourceId);
        if (index < 0)
        {
            Tabs.Add(tab);
        }
        else
        {
            // 就地替换，标签条不重排：同一台 VM 的会话换个新的，位置保持不变
            var previous = Tabs[index];
            previous.Dispose();
            Tabs[index] = tab;
            _ = DisposeSessionAsync(previous, "替换会话");
        }

        ActiveTab = tab;
    }

    /// <summary>该 VM 是否已经有面板标签。批量连接据此改为"只激活、不重连"。</summary>
    public bool HasLiveTabFor(string vmResourceId) => IndexOfTab(vmResourceId) >= 0;

    /// <summary>把某个 VM 的既有标签切到前台（批量连接对已在会话中的 VM 走这条，不重连）。</summary>
    public bool ActivateExisting(string vmResourceId)
    {
        var index = IndexOfTab(vmResourceId);
        if (index < 0)
        {
            return false;
        }

        IsExpanded = true;
        ActiveTab = Tabs[index];
        return true;
    }

    [RelayCommand]
    private void ActivateTab(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        IsExpanded = true;
        ActiveTab = tab;
    }

    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // 先选定邻居再移除：移除会让 ListBox 的 SelectedItem 落空，
        // 若不接管就会变成"关掉一个标签后什么都不选中"，而不是切到旁边的会话
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count > 1
                ? Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1]
                : null;
        }

        Tabs.Remove(tab);
        _ = DisposeSessionAsync(tab, "关闭标签");
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>抓手拖动：向下拖（<paramref name="verticalChange"/> 为正）面板变矮。</summary>
    public void ResizeBy(double verticalChange, double bodyHeight)
    {
        _bodyHeight = bodyHeight;

        if (FillsBody)
        {
            // 拖动即退出最大化/全屏，并回到进入<em>之前</em>的高度再加这次增量：
            // 直接对满高做减法会一步跳到"页面最小高度"那个上限，手感像被甩了一下
            IsMaximized = false;
            IsFullScreen = false;
            PanelHeight = ClampPanelHeight(_restoreHeight, bodyHeight);
        }

        PanelHeight = ClampPanelHeight(PanelHeight - verticalChange, bodyHeight);
    }

    /// <summary>主体尺寸变化后重新钳制，避免窗口变矮时面板把页面挤没、底边跑出窗口。</summary>
    public void ClampToBody(double bodyHeight)
    {
        _bodyHeight = bodyHeight;
        PanelHeight = FillsBody
            ? MaxPanelHeight(bodyHeight)
            : ClampPanelHeight(PanelHeight, bodyHeight);
    }

    /// <summary>最大化 / 还原：面板占满主体区，页面区让位（顶栏与左导航保留）。</summary>
    [RelayCommand]
    private void ToggleMaximize()
    {
        // 全屏 → 最大化是"退一档"：顶栏与导航回来，但面板仍然是满的。
        // 不这么做的话，全屏时点最大化会一步退回原始高度，跨了两档
        if (IsFullScreen)
        {
            IsFullScreen = false;
            IsMaximized = true;
            return;
        }

        if (IsMaximized || !EnterFill())
        {
            RestoreFromFill();
            return;
        }

        IsMaximized = true;
    }

    /// <summary>全屏 / 还原：面板连同顶栏与左导航一起让位，铺满整个窗口。</summary>
    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (IsFullScreen || !EnterFill())
        {
            RestoreFromFill();
            return;
        }

        IsMaximized = false;
        IsFullScreen = true;
    }

    /// <summary>
    /// 进入填满状态前的公共准备：记住原高度、确保展开、按满高取值。
    /// 返回 false 表示布局还没跑过（算不出满高），调用方应当整个放弃这次点击 ——
    /// 只置标志位而不改高度会让"填满"这个状态名不副实。
    /// </summary>
    private bool EnterFill()
    {
        if (_bodyHeight <= 0)
        {
            return false;
        }

        _restoreHeight = PanelHeight;
        IsExpanded = true;
        PanelHeight = MaxPanelHeight(_bodyHeight);
        return true;
    }

    private void RestoreFromFill()
    {
        IsMaximized = false;
        IsFullScreen = false;
        PanelHeight = ClampPanelHeight(_restoreHeight, _bodyHeight);
    }

    /// <summary>占满主体区所需的高度：扣掉抓手，剩下的全归面板（页面区让到 0）。</summary>
    private static double MaxPanelHeight(double bodyHeight) =>
        bodyHeight > 0 ? Math.Max(MinPanelHeight, bodyHeight - GripHeight) : DefaultPanelHeight;

    private static double ClampPanelHeight(double height, double bodyHeight)
    {
        // 布局尚未跑过时 bodyHeight 为 0，此时不设上限（否则会被钳到最小值）
        var max = bodyHeight > 0
            ? Math.Max(MinPanelHeight, bodyHeight - GripHeight - PageMinHeight)
            : double.MaxValue;
        return Math.Clamp(height, MinPanelHeight, max);
    }

    /// <summary>
    /// 退出前的收尾：拆掉全部视图、释放全部会话，最多由调用方控时。
    /// </summary>
    public async Task ShutdownAsync()
    {
        // 先<b>同步</b>清空集合：视图的 CollectionChanged(Reset) 会同步拆掉所有终端控件，
        // 于是"先销毁渲染器、再关会话"的顺序自动成立，不会有事件回写到已经拆掉的 WebView。
        var tabs = Tabs.ToArray();
        Tabs.Clear();
        ActiveTab = null;
        IsExpanded = false;

        foreach (var tab in tabs)
        {
            tab.Dispose();
        }

        await Task.WhenAll(tabs.Select(t => DisposeSessionAsync(t, "退出收尾")));
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(TabsTip));

        // 没有会话就没有面板：收起后顶栏入口也随 HasTabs 一起消失，
        // 「连接」成为唯一的回来路径（用户已定：完全隐藏，底部不留残留）
        if (Tabs.Count == 0)
        {
            ActiveTab = null;
            IsExpanded = false;
            // 一起清掉最大化/全屏：否则下一个会话打开时会直接占满全窗（全屏时连顶栏和
            // 导航都不在），而用户从没这么要求过
            IsMaximized = false;
            IsFullScreen = false;
        }
    }

    /// <summary>
    /// 释放一条会话。<c>DisposeAsync()</c> 已含 <c>CloseAsync()</c> 的全部清理，
    /// 只调一次即可（两个都调会重复清理）。
    /// </summary>
    private async Task DisposeSessionAsync(TerminalTabViewModel tab, string reason)
    {
        try
        {
            await tab.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            // 会话释放失败不阻断其余会话与关窗；但也绝不静默 —— 记进日志
            _logger.LogWarning(ex, "SSH 会话释放失败（{Reason}）：{Target}", reason, tab.Target);
        }
    }

    private int IndexOfTab(string vmResourceId)
    {
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (string.Equals(Tabs[i].VmResourceId, vmResourceId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
