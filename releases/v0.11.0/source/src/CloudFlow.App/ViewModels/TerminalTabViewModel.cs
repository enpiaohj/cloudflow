using CloudFlow.Terminal.Ssh;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 底部终端面板里的一个会话标签。
/// <para>
/// 纯状态对象，不持有 <c>SshTerminalView</c>：控件生命周期归视图层（<c>TerminalPanel.xaml.cs</c>），
/// 这样这个 VM 可以在没有 WPF 树的测试里被构造与检查。
/// </para>
/// <para>
/// 标签身份 = VM 的 <c>ResourceId</c>，同一台 VM 只对应一个标签；
/// 再次连接走"复用标签、替换会话"，见 <see cref="TerminalPanelViewModel.OpenOrActivate"/>。
/// </para>
/// </summary>
public sealed partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    public TerminalTabViewModel(string vmResourceId, string vmName, string host, SshSession session)
    {
        VmResourceId = vmResourceId;
        VmName = vmName;
        Host = host;
        Session = session;
        _statusText = Describe(session.State);

        session.StateChanged += OnSessionStateChanged;
    }

    /// <summary>去重键（VM 主键，与凭据存储的键一致）。</summary>
    public string VmResourceId { get; }

    public string VmName { get; }

    public string Host { get; }

    public SshSession Session { get; }

    /// <summary>形如 <c>azureuser@20.0.0.1:22</c>，标签与顶栏按钮的 ToolTip 用它。</summary>
    public string Target => Session.Target;

    /// <summary>该标签当前是否在面板中显示。切换标签时由视图逐个设置可见性。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>标签上的短状态文案（见 <see cref="Describe"/>）。</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>状态细节（失败原因），只在悬停时展示，不挤占标签宽度。</summary>
    [ObservableProperty]
    private string? _statusTip;

    /// <summary>
    /// 状态文案保持短：标签条高度固定 36px、宽度还要留给并列的其它会话，
    /// 把一整条 SSH 错误消息塞进标签会把整条标签条挤变形 —— 详情走 <see cref="StatusTip"/>。
    /// </summary>
    private static string Describe(SshSessionState state) => state switch
    {
        SshSessionState.Idle => "待连接",
        SshSessionState.Connecting => "连接中…",
        SshSessionState.Connected => "已连接",
        SshSessionState.Disconnected => "已断开",
        SshSessionState.Failed => "连接失败",
        SshSessionState.Closed => "已关闭",
        _ => state.ToString()
    };

    /// <summary>
    /// <c>SshSession.StateChanged</c> 由 SSH 读线程触发（与 DataReceived 同源），
    /// 直接回写会抛跨线程异常，必须封送到 UI 线程。
    /// </summary>
    private void OnSessionStateChanged(object? sender, SshSessionState state)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(state);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => Apply(state)));
    }

    private void Apply(SshSessionState state)
    {
        if (_disposed)
        {
            return;
        }

        StatusText = Describe(state);
        StatusTip = state == SshSessionState.Failed ? Session.ErrorMessage : null;
    }

    /// <summary>只退订状态事件，<b>不碰会话</b> —— 会话由面板统一释放（避免两处释放同一会话）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.StateChanged -= OnSessionStateChanged;
    }
}
