using System.IO;
using CloudFlow.Terminal.Security;

namespace CloudFlow.Terminal.Ssh;

/// <summary>SSH 握手阶段出示的主机密钥信息（含与本地记录的比对结果）。</summary>
public sealed class SshHostKey
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string KeyAlgorithm { get; init; }
    /// <summary>远端本次出示的指纹（SHA256）。</summary>
    public required string Fingerprint { get; init; }
    /// <summary>本地已记录的指纹；首次连接为 null。</summary>
    public string? KnownFingerprint { get; init; }

    public bool IsKnownGood => KnownFingerprint is not null && KnownFingerprint == Fingerprint;
    /// <summary>本地有记录但与本次出示不一致 —— 可能存在中间人风险，UI 必须强警告。</summary>
    public bool IsMismatch => KnownFingerprint is not null && KnownFingerprint != Fingerprint;
}

/// <summary>
/// Host Key 两段式校验策略（照 RemoteFlow 逻辑）：
/// <list type="number">
/// <item><see cref="Lookup"/>：握手线程上同步查询本地记录，<b>不弹任何 UI</b>。
/// 未信任时由 SSH 会话中止握手（此时凭据尚未发送），把待确认的密钥记下来。</item>
/// <item><see cref="ConfirmAndRememberAsync"/>：连接失败后由 UI 线程弹窗确认；
/// 用户接受则记录指纹并重试，拒绝则终态失败。绝不静默信任。</item>
/// </list>
/// </summary>
public sealed class SshHostKeyPolicy
{
    private readonly KnownHostStore _store;

    /// <summary>UI 确认回调：展示指纹供用户决策，返回 true = 信任并记住。</summary>
    private readonly Func<SshHostKey, Task<bool>> _confirm;

    public SshHostKeyPolicy(KnownHostStore store, Func<SshHostKey, Task<bool>> confirm)
    {
        _store = store;
        _confirm = confirm;
    }

    /// <summary>
    /// 查询本地记录并比对指纹。SSH.NET 的 HostKeyReceived 在握手线程上同步触发，
    /// 这里的小文件读用同步完成（RemoteFlow 的 SQLite Lookup 同理），不弹任何 UI。
    /// </summary>
    public SshHostKey Lookup(string host, int port, string keyAlgorithm, string fingerprint)
    {
        var known = _store.FindAsync(host, port).GetAwaiter().GetResult();
        return new SshHostKey
        {
            Host = host,
            Port = port,
            KeyAlgorithm = keyAlgorithm,
            Fingerprint = fingerprint,
            KnownFingerprint = known?.Fingerprint
        };
    }

    /// <summary>用户接受后记录指纹（同主机端口覆盖）。返回值透传用户的选择。</summary>
    public async Task<bool> ConfirmAndRememberAsync(SshHostKey info, CancellationToken ct = default)
    {
        var accepted = await _confirm(info);
        if (!accepted)
        {
            return false;
        }

        await _store.AddAsync(new KnownHostEntry
        {
            Host = info.Host,
            Port = info.Port,
            KeyAlgorithm = info.KeyAlgorithm,
            Fingerprint = info.Fingerprint,
            AddedAt = DateTimeOffset.Now
        }, ct);
        return true;
    }
}
