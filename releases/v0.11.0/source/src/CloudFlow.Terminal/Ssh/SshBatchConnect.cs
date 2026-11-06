using CloudFlow.Terminal.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.Terminal.Ssh;

/// <summary>
/// 批量连接的一个目标。**刻意与 <c>VmSummary</c> 解耦** —— 那个类型在
/// <c>CloudFlow.Modules.Compute</c> 里，让 <c>CloudFlow.Terminal</c> 依赖它是层次倒挂。
/// 由 App 层负责把 VM 映射成这个中性形态。
/// </summary>
/// <param name="VmResourceId">去重键（与终端面板的标签身份一致）。</param>
/// <param name="DisplayName">汇总与进度里显示的名字。</param>
/// <param name="Host">连接地址（公网 IP 优先）。</param>
/// <param name="IsConnectable"><c>false</c> = 批量不处理它（当前只有一种：非 Linux）。</param>
/// <param name="Port">SSH 端口。跟着目标走而不是从共享 template 取 —— 逐台参数各不相同之后，
/// 已经没有一个"全局端口"可用了（探测轮要在建 options 之前查 known-hosts，正需要它）。</param>
public sealed record SshBatchTarget(
    string VmResourceId, string DisplayName, string Host, bool IsConnectable, int Port = 22);

/// <summary>批量连接单条结果的去向。</summary>
public enum SshBatchItemState
{
    /// <summary>本次新建了会话并连上。</summary>
    Connected,

    /// <summary>该 VM 已有会话，只切到前台，没有重连。</summary>
    AlreadyConnected,

    /// <summary>按规则跳过（非 Linux、无 IP、用户取消）。</summary>
    Skipped,

    /// <summary>尝试了但失败。</summary>
    Failed
}

/// <summary>批量连接里一台机器的结果。<b>不含任何秘密，也不含底层异常文本。</b></summary>
/// <param name="Message">面向用户的中文说明。取自 <see cref="SshConnectionErrorText.Describe"/>
/// 或本类里的固定串 —— <b>绝不来自 <c>ex.Message</c> / <c>ex.ToString()</c></b>。</param>
public sealed record SshBatchItemResult(
    SshBatchTarget Target,
    SshBatchItemState State,
    SshConnectionErrorCode ErrorCode,
    string Message);

/// <summary>批量连接的阶段（进度用）。</summary>
public enum SshBatchPhase
{
    /// <summary>探测未知主机的指纹（要握手，可能较慢）。</summary>
    Probing,

    /// <summary>等待用户在批量确认框里决定。</summary>
    AwaitingHostKeyConfirmation,

    /// <summary>逐台建立会话。</summary>
    Connecting
}

/// <summary>批量连接进度。</summary>
public sealed record SshBatchProgress(SshBatchPhase Phase, int Completed, int Total, string TargetName);

/// <summary>一次批量连接的结果汇总。</summary>
public sealed class SshBatchReport
{
    public SshBatchReport(IReadOnlyList<SshBatchItemResult> items)
    {
        Items = items;

        var connected = items.Count(i => i.State == SshBatchItemState.Connected);
        var reused = items.Count(i => i.State == SshBatchItemState.AlreadyConnected);
        var skipped = items.Count(i => i.State == SshBatchItemState.Skipped);
        var failed = items.Where(i => i.State == SshBatchItemState.Failed).ToList();

        HasFailure = failed.Count > 0;

        var parts = new List<string>();
        if (connected > 0)
        {
            parts.Add($"已连接 {connected} 台");
        }

        if (reused > 0)
        {
            parts.Add($"复用已有会话 {reused} 台");
        }

        if (skipped > 0)
        {
            parts.Add($"跳过 {skipped} 台");
        }

        if (failed.Count > 0)
        {
            parts.Add($"失败 {failed.Count} 台");
        }

        var headline = parts.Count == 0 ? "没有可连接的虚拟机。" : string.Join("，", parts) + "。";

        // 失败逐条列出原因 —— 一句话"有 N 台失败"对排查毫无帮助
        var detail = failed.Count == 0
            ? string.Empty
            : " " + string.Join("；", failed.Select(f => $"{f.Target.DisplayName}：{f.Message}"));

        // 跳过也要说清是为什么跳过的（"跳过 1 台"本身不构成告知）
        var skipDetail = skipped == 0
            ? string.Empty
            : " " + string.Join("；", items
                .Where(i => i.State == SshBatchItemState.Skipped)
                .Select(i => $"{i.Target.DisplayName}：{i.Message}"));

        Summary = headline + skipDetail + detail;
    }

    public IReadOnlyList<SshBatchItemResult> Items { get; }

    /// <summary>一句可直接放进 InfoBar 的中文汇总。</summary>
    public string Summary { get; }

    public bool HasFailure { get; }
}

/// <summary>
/// 逐台构建连接参数。<b>策略由编排层通过 <paramref name="policy"/> 注入</b> ——
/// 探测轮与连接轮需要不同的策略（见 <see cref="SshBatchConnector"/> 的类注释），
/// 所以调用方不该自己决定它，否则"探测轮零写入"这条不变量会取决于外部实现。
/// </summary>
/// <remarks>
/// 收工厂而不是收一个共享 template，是因为用户已定：<b>每台虚拟机各用自己记住的那条凭据</b>。
/// 于是同一批里不同目标的用户名 / 口令 / 私钥都可能不同。
/// </remarks>
public delegate SshConnectionOptions SshOptionsFactory(SshBatchTarget target, SshHostKeyPolicy policy);

/// <summary>
/// 终端面板的抽象。存在的唯一理由是让批量编排<b>可以被单测</b> ——
/// <c>CloudFlow.App.Tests</c> 是 net8.0 零项目引用、够不到 App 程序集，
/// 编排若写在 App 层就等于零覆盖。
/// </summary>
public interface ISshSessionHost
{
    bool HasLiveTabFor(string vmResourceId);

    bool ActivateExisting(string vmResourceId);

    /// <summary>
    /// 探测该主机出示的指纹：建立一个<b>不进 UI、不留标签</b>的一次性会话并握手。
    /// 实现方不得写入任何指纹记录 —— 要不要信任由 <paramref name="options"/> 里的
    /// <see cref="SshHostKeyPolicy"/> 决定，而调用方传进来的那个恒返回 <c>false</c>。
    /// </summary>
    Task ProbeHostKeyAsync(SshBatchTarget target, SshConnectionOptions options, CancellationToken ct);

    /// <summary>开一个会话标签并把它接上；<b>内部 await 到有结论</b>才返回。</summary>
    Task<SshSessionOutcome> OpenAndConnectAsync(
        SshBatchTarget target, SshConnectionOptions options, CancellationToken ct);
}

/// <summary>
/// 一条会话的建立结论。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>返回 <see cref="SshSession"/> 本身：它是 <c>sealed</c> 且 <c>State</c> 是私有 setter，
/// 假实现造不出"已连接"的会话，成功路径就会变成不可测。这里只要编排需要的两个事实。
/// </remarks>
public sealed record SshSessionOutcome(bool Started, SshConnectionErrorCode ErrorCode);

/// <summary>
/// 批量连接的编排：去重 → 剔不可连 → 探测未知主机 → 合并确认 → <b>一次性写入</b> → 串行连接 → 汇总。
/// </summary>
/// <remarks>
/// <para><b>主机指纹的纪律（本类的核心）：</b></para>
/// <list type="number">
/// <item>探测轮用<b>恒返回 <c>false</c></b> 的确认回调 —— <c>ConfirmAndRememberAsync</c> 只在
/// <c>accepted == true</c> 时才写 store，所以这一轮<b>零写入</b>。</item>
/// <item>只有用户明确点了「全部信任并继续」，本类才把<b>恰好那几条</b>写进
/// <see cref="KnownHostStore"/>。<b>这是整条链路上唯一一次指纹写入</b>，也就是"明确同意"的落点。</item>
/// <item>连接轮用<b>失败即拒</b>（<c>_confirm</c> 恒 <c>false</c>）的策略，而不是交互式的那个：
/// 探测轮没写盘，若连接轮用交互策略就会把用户刚确认过的指纹<b>再弹一次框</b>；
/// 用恒 false 则已被接受的指纹已在 store 里、<c>Lookup</c> 直接 <c>IsKnownGood</c>、回调根本不触发。
/// 万一探测与连接之间指纹真被换掉，回调触发后返回 false，该条以 <c>HostKeyMismatch</c> 失败。
/// <b>既无静默接受，也无隐藏的模态框。</b></item>
/// </list>
/// <para><b>线程</b>：本类不自建线程，全部在调用方（UI 线程）的上下文中 await，
/// 进度回调因此直接落在 UI 线程上。</para>
/// </remarks>
public sealed class SshBatchConnector
{
    private readonly KnownHostStore _knownHosts;
    private readonly ISshSessionHost _host;
    private readonly Func<IReadOnlyList<SshHostKey>, CancellationToken, Task<bool>> _confirmNewHosts;
    private readonly IProgress<SshBatchProgress>? _progress;
    private readonly ILogger _logger;

    /// <param name="confirmNewHosts">把"首次遇到的主机密钥"交给用户决定；返回 true = 全部信任并继续。
    /// 返回 false 时整批不连，且<b>不会写入任何指纹</b>。</param>
    public SshBatchConnector(
        KnownHostStore knownHosts,
        ISshSessionHost host,
        Func<IReadOnlyList<SshHostKey>, CancellationToken, Task<bool>> confirmNewHosts,
        IProgress<SshBatchProgress>? progress = null,
        ILogger? logger = null)
    {
        _knownHosts = knownHosts;
        _host = host;
        _confirmNewHosts = confirmNewHosts;
        _progress = progress;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<SshBatchReport> RunAsync(
        IReadOnlyList<SshBatchTarget> targets,
        SshOptionsFactory optionsFor,
        CancellationToken ct = default)
    {
        var results = new List<SshBatchItemResult>();

        // ── 1. 去重 + 剔掉不可连的 ──
        var candidates = new List<SshBatchTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (!seen.Add(target.VmResourceId))
            {
                continue;   // 重复项会让面板走"替换会话"路径、把先建的那条杀掉，必须在这里挡住
            }

            if (!target.IsConnectable)
            {
                results.Add(new SshBatchItemResult(target, SshBatchItemState.Skipped,
                    SshConnectionErrorCode.None, "非 Linux 虚拟机，批量连接只支持 Linux（Windows 请单独连接）"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(target.Host))
            {
                results.Add(new SshBatchItemResult(target, SshBatchItemState.Skipped,
                    SshConnectionErrorCode.None, "没有可用的 IP 地址"));
                continue;
            }

            candidates.Add(target);
        }

        if (candidates.Count == 0)
        {
            return new SshBatchReport(results);
        }

        // ── 2. 探测轮：只为"本机没有记录"的目标取指纹，零写入 ──
        // 指纹变化不在这里出现（原因见 ProbeUnknownHostsAsync 的注释），它在连接轮被"失败即拒"挡下。
        var newKeys = await ProbeUnknownHostsAsync(candidates, optionsFor, ct);

        // ── 3. 合并确认 ──
        if (newKeys.Count > 0)
        {
            _progress?.Report(new SshBatchProgress(
                SshBatchPhase.AwaitingHostKeyConfirmation, 0, newKeys.Count, string.Empty));

            if (!await _confirmNewHosts(newKeys, ct))
            {
                // 用户拒绝：整批不连，且一条指纹都不写
                foreach (var target in candidates)
                {
                    results.Add(new SshBatchItemResult(target, SshBatchItemState.Skipped,
                        SshConnectionErrorCode.HostKeyRejected, "已取消：未信任新主机的指纹"));
                }

                return new SshBatchReport(results);
            }

            // ── 4. 唯一一次指纹写入：恰好写用户接受的那几条 ──
            foreach (var key in newKeys)
            {
                await _knownHosts.AddAsync(new KnownHostEntry
                {
                    Host = key.Host,
                    Port = key.Port,
                    KeyAlgorithm = key.KeyAlgorithm,
                    Fingerprint = key.Fingerprint,
                    AddedAt = DateTimeOffset.Now
                }, ct);
            }
        }

        // ── 5. 串行连接 ──
        // 失败即拒：见类注释。已在会话中的只激活、不重连。
        var failClosed = new SshHostKeyPolicy(_knownHosts, _ => Task.FromResult(false));

        for (var index = 0; index < candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var target = candidates[index];
            _progress?.Report(new SshBatchProgress(
                SshBatchPhase.Connecting, index, candidates.Count, target.DisplayName));

            if (_host.HasLiveTabFor(target.VmResourceId))
            {
                _host.ActivateExisting(target.VmResourceId);
                results.Add(new SshBatchItemResult(target, SshBatchItemState.AlreadyConnected,
                    SshConnectionErrorCode.None, "已有会话，已切到前台"));
                continue;
            }

            var options = optionsFor(target, failClosed);

            try
            {
                var outcome = await _host.OpenAndConnectAsync(target, options, ct);

                if (outcome.Started)
                {
                    results.Add(new SshBatchItemResult(target, SshBatchItemState.Connected,
                        SshConnectionErrorCode.None, "已连接"));
                }
                else
                {
                    results.Add(new SshBatchItemResult(target, SshBatchItemState.Failed,
                        outcome.ErrorCode, DescribeFailure(outcome.ErrorCode)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 异常只进日志（排查用），**不进面向用户的 Message** ——
                // 异常消息是"日志/异常消息不得含 Secret"那条红线最容易破的入口。
                _logger.LogWarning(ex, "批量连接 {Target} 时发生异常", target.DisplayName);
                results.Add(new SshBatchItemResult(target, SshBatchItemState.Failed,
                    SshConnectionErrorCode.Unknown, "发生未知错误"));
            }
        }

        return new SshBatchReport(results);
    }

    /// <summary>
    /// 失败原因（面向用户）。
    /// </summary>
    /// <remarks>
    /// 指纹不一致单独措辞：批量<b>刻意不</b>处理它（中间人信号必须单独看、单独决策），
    /// 所以要讲清"这里为什么没连、接下来该怎么办"，而不是丢一句通用的认证失败让用户去猜。
    /// </remarks>
    private static string DescribeFailure(SshConnectionErrorCode code) =>
        code == SshConnectionErrorCode.HostKeyMismatch
            ? "主机指纹与本机已记录的不一致（可能存在中间人风险）；批量不处理此项，请单独连接并核对"
            : SshConnectionErrorText.Describe(code);

    /// <summary>
    /// 探测轮。只为<b>本机 <see cref="KnownHostStore"/> 里没有记录</b>的目标握手 ——
    /// 已有记录的主机没必要探测（既省时间，也避免对已知主机发起额外连接）。
    /// </summary>
    /// <remarks>
    /// <b>指纹变化（<see cref="SshHostKey.IsMismatch"/>）不在这里分流</b>，尽管它看起来该在这儿：
    /// <see cref="SshHostKey.KnownFingerprint"/> 来自 store，而 store 里<b>有</b>记录的主机
    /// 在这一轮已经被跳过；没记录 ⇒ <c>KnownFingerprint</c> 为 <c>null</c> ⇒ <c>IsMismatch</c> 恒为 false。
    /// 所以"指纹变了"只可能在<b>连接轮</b>出现，由那里的"失败即拒"策略挡下（见
    /// <see cref="RunAsync"/>）。在这里留一个恒不可达的分支，只会让人以为这里处理过。
    /// </remarks>
    private async Task<IReadOnlyList<SshHostKey>> ProbeUnknownHostsAsync(
        IReadOnlyList<SshBatchTarget> candidates, SshOptionsFactory optionsFor, CancellationToken ct)
    {
        var newKeys = new List<SshHostKey>();

        for (var index = 0; index < candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var target = candidates[index];

            // 已有会话的目标在连接轮只会被激活、不会重连 —— 不必为它付一次探测握手
            if (_host.HasLiveTabFor(target.VmResourceId))
            {
                continue;
            }

            if (await _knownHosts.FindAsync(target.Host, target.Port, ct) is not null)
            {
                continue;
            }

            _progress?.Report(new SshBatchProgress(
                SshBatchPhase.Probing, index, candidates.Count, target.DisplayName));

            // 恒返回 false ⇒ ConfirmAndRememberAsync 走 accepted==false 分支 ⇒ 一个字节都不写
            SshHostKey? captured = null;
            var probePolicy = new SshHostKeyPolicy(_knownHosts, key =>
            {
                captured = key;
                return Task.FromResult(false);
            });

            try
            {
                await _host.ProbeHostKeyAsync(target, optionsFor(target, probePolicy), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 探测失败（主机不可达等）不是致命错误：会话真的连不上时连接轮会再报一次，
                // 这里只记日志。**不写入任何记录。**
                _logger.LogDebug(ex, "探测 {Target} 的主机指纹失败", target.DisplayName);
            }

            if (captured is { } key)
            {
                newKeys.Add(key);
            }
        }

        return newKeys;
    }

}
