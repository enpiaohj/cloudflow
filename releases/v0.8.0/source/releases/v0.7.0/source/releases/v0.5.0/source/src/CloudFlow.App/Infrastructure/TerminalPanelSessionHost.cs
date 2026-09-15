using CloudFlow.App.ViewModels;
using CloudFlow.Terminal.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 把 <see cref="TerminalPanelViewModel"/> 适配成批量编排需要的
/// <see cref="ISshSessionHost"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类存在的理由是一条隐性契约</b>（写在 <c>TerminalPanel.xaml.cs</c> 顶部）：
/// 面板只为<b>被激活过</b>的标签创建 <c>SshTerminalView</c>，而连接是那个控件在前端
/// <c>ready</c> 之后才发起的 —— <b>没被激活过的标签永远不会连接</b>。
/// </para>
/// <para>
/// 所以"完全串行"不能靠在编排层循环调 <c>OpenOrActivate</c> 实现，必须由这里
/// <b>await 到这条会话有结论</b>才返回。<c>SshSession.ConnectAsync</c> 返回 <c>Task</c>，
/// 正是这个 await 的落点。
/// </para>
/// </remarks>
public sealed class TerminalPanelSessionHost : ISshSessionHost
{
    private readonly TerminalPanelViewModel _panel;
    private readonly ILogger _logger;

    public TerminalPanelSessionHost(
        TerminalPanelViewModel panel, ILogger<TerminalPanelSessionHost>? logger = null)
    {
        _panel = panel;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool HasLiveTabFor(string vmResourceId) => _panel.HasLiveTabFor(vmResourceId);

    public bool ActivateExisting(string vmResourceId) => _panel.ActivateExisting(vmResourceId);

    public async Task ProbeHostKeyAsync(
        SshBatchTarget target, SshConnectionOptions options, CancellationToken ct)
    {
        // 一次性会话：**不进面板、不留标签**，探测完就丢。
        // 唯一目的是让握手把主机密钥交出来 —— 交给 options.HostKeyPolicy 里那个
        // 恒返回 false 的回调，所以这条路径**一个字节都不会写进 KnownHostStore**。
        await using var session = new SshSession(options, NullLogger<SshSession>.Instance);

        try
        {
            await session.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            // 探测失败（主机不可达等）不算致命：编排层只关心"有没有拿到指纹"，
            // 而那是由 HostKeyPolicy 的回调体现的，不靠这里的异常。
            // 真正的连接失败会在连接轮再报一次，那条才是要给用户看的。
            _logger.LogDebug(ex, "探测 {Target} 的主机密钥失败", target.DisplayName);
        }
    }

    public async Task<SshSessionOutcome> OpenAndConnectAsync(
        SshBatchTarget target, SshConnectionOptions options, CancellationToken ct)
    {
        _panel.OpenOrActivate(target.VmResourceId, target.DisplayName, target.Host, options);

        var tab = _panel.Tabs.FirstOrDefault(candidate =>
            string.Equals(candidate.VmResourceId, target.VmResourceId, StringComparison.OrdinalIgnoreCase));

        if (tab is null)
        {
            return new SshSessionOutcome(false, SshConnectionErrorCode.Unknown);
        }

        var session = tab.Session;

        if (session.State is not SshSessionState.Idle)
        {
            // 不该发生：OpenOrActivate 是同步的，而视图要等 WebView2 就绪才会发起连接，
            // 所以这个 await 必然由我们发起、并一直等到有结论。
            // 真走到这里说明"谁先连"的前提被破坏了 —— 宁可留一行日志，也不要把它静默读成失败。
            _logger.LogWarning(
                "会话在发起前已处于 {State} 状态，串行等待的前提可能已不成立", session.State);
        }

        await session.ConnectAsync(ct);

        return session.State == SshSessionState.Connected
            ? new SshSessionOutcome(true, SshConnectionErrorCode.None)
            : new SshSessionOutcome(false, session.ErrorCode);
    }
}
