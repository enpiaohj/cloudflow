using System.IO;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CloudFlow.Terminal.Ssh;

/// <summary>
/// SSH 会话（参考 RemoteFlow 连接逻辑的裁剪版）。
/// <para>
/// <b>分层原则</b>：本类只负责 SSH 协议连接与原始字节收发——
/// ANSI 转义解析、解码与滚动缓冲由终端渲染层（xterm.js）负责。
/// 因此与上层只交换原始字节：多字节字符被 TCP 分包切断也不会产生乱码。
/// </para>
/// </summary>
public sealed class SshSession : IAsyncDisposable
{
    private readonly SshConnectionOptions _options;
    private readonly ILogger _logger;

    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;

    /// <summary>会话级取消源：连接中关闭会话立刻中断握手，不干等库超时。</summary>
    private readonly CancellationTokenSource _lifecycleCts = new();

    /// <summary>保护断开/释放路径，避免 UI 关窗与远端断线同时触发导致重复释放。</summary>
    private readonly SemaphoreSlim _lifecycleMutex = new(1, 1);

    private SshConnectionErrorCode _hostKeyFailure = SshConnectionErrorCode.None;
    private SshHostKey? _pendingHostKey;
    private volatile bool _disposed;
    private volatile bool _closing;

    public SshSession(SshConnectionOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    public SshSessionState State { get; private set; } = SshSessionState.Idle;

    public SshConnectionErrorCode ErrorCode { get; private set; } = SshConnectionErrorCode.None;

    public string? ErrorMessage { get; private set; }

    /// <summary>状态变化（UI 状态条订阅）。</summary>
    public event EventHandler<SshSessionState>? StateChanged;

    /// <summary>远端输出。原始字节，交由终端渲染层解码。</summary>
    public event EventHandler<byte[]>? DataReceived;

    public string Target => $"{_options.Username}@{_options.Host}:{_options.Port}";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _closing || State is SshSessionState.Connecting or SshSessionState.Connected)
        {
            return;
        }

        SetState(SshSessionState.Connecting);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifecycleCts.Token);
            var connectCt = linkedCts.Token;

            // 最多两轮：首轮若因主机密钥未信任被中止（凭据尚未发送），
            // 弹确认框（HostKeyPolicy.ConfirmAndRememberAsync）后重试一轮。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await ConnectOnceAsync(connectCt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    await CleanupAsync();
                    if (!_closing && !_disposed &&
                        State is not (SshSessionState.Disconnected or SshSessionState.Failed))
                    {
                        Fail(SshConnectionErrorCode.Cancelled, null);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    await CleanupAsync();
                    if (_closing || _disposed)
                    {
                        return;
                    }

                    // 首轮被 HostKey 策略中止（已记下待确认的密钥）→ 弹确认，接受则重试
                    if (attempt == 0 && _pendingHostKey is { } pending)
                    {
                        _pendingHostKey = null;
                        var accepted = await _options.HostKeyPolicy.ConfirmAndRememberAsync(
                            pending, cancellationToken);
                        if (accepted)
                        {
                            _hostKeyFailure = SshConnectionErrorCode.None;
                            continue;
                        }

                        Fail(pending.IsMismatch
                            ? SshConnectionErrorCode.HostKeyMismatch
                            : SshConnectionErrorCode.HostKeyRejected, null);
                        return;
                    }

                    Fail(SshConnectionErrorText.Map(ex, _hostKeyFailure), ex);
                    return;
                }
            }
        }
        finally
        {
            // 无论成败，凭据都不再需要，立即释放引用（Secret 由调用方处置）。
        }
    }

    /// <summary>向远端发送用户输入（原始字节）。未连接时静默丢弃。</summary>
    public void SendInput(byte[] data)
    {
        var shell = _shell;
        if (shell is null || data.Length == 0 || State != SshSessionState.Connected)
        {
            return;
        }

        try
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 发送输入失败", SessionId);
        }
    }

    /// <summary>调整远端终端尺寸（窗口大小变化时由渲染层调用）。</summary>
    public void Resize(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        var shell = _shell;
        if (shell is null || State != SshSessionState.Connected || columns == 0 || rows == 0)
        {
            return;
        }

        try
        {
            shell.ChangeWindowSize(columns, rows, pixelWidth, pixelHeight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH 会话 {SessionId} 调整终端尺寸失败", SessionId);
        }
    }

    /// <summary>断开并清理。幂等；窗口关闭与「断开」按钮共用。</summary>
    public async Task CloseAsync()
    {
        _closing = true;
        CancelLifecycle();
        await CleanupAsync();

        if (State is not (SshSessionState.Failed or SshSessionState.Disconnected or SshSessionState.Closed))
        {
            SetState(SshSessionState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing = true;
        CancelLifecycle();
        await CleanupAsync();
        _lifecycleMutex.Dispose();
    }

    // ── 连接 ─────────────────────────────────────────────────────

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _pendingHostKey = null;
        _hostKeyFailure = SshConnectionErrorCode.None;

        var connectionInfo = BuildConnectionInfo();
        _client = new SshClient(connectionInfo);
        _client.HostKeyReceived += OnHostKeyReceived;

        await _client.ConnectAsync(ct);

        _shell = _client.CreateShellStream("xterm-256color", 80, 24, 800, 600, 16 * 1024);
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_shell, _readLoopCts.Token), CancellationToken.None);

        SetState(SshSessionState.Connected);
        _logger.LogInformation("SSH 会话 {SessionId} 已连接 {Target}", SessionId, Target);
    }

    private Renci.SshNet.ConnectionInfo BuildConnectionInfo()
    {
        var username = _options.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("SSH 连接必须指定用户名。");
        }

        Renci.SshNet.AuthenticationMethod authentication = _options.AuthType switch
        {
            Security.SshAuthType.PrivateKey => BuildPrivateKeyAuthentication(username),
            _ => new PasswordAuthenticationMethod(username, _options.Password ?? string.Empty)
        };

        return new Renci.SshNet.ConnectionInfo(_options.Host, _options.Port, username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(_options.ConnectTimeoutSeconds, 5))
        };
    }

    private Renci.SshNet.PrivateKeyAuthenticationMethod BuildPrivateKeyAuthentication(string username)
    {
        if (string.IsNullOrEmpty(_options.PrivateKey))
        {
            throw new InvalidOperationException("该连接配置了私钥认证，但未提供私钥内容。");
        }

        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(_options.PrivateKey));
            var keyFile = string.IsNullOrEmpty(_options.Passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, _options.Passphrase);
            return new PrivateKeyAuthenticationMethod(username, keyFile);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ArgumentException)
        {
            // 异常信息不含私钥内容，可安全传递；这里换成语义化中文。
            throw new InvalidOperationException("SSH 私钥无法加载，请检查私钥格式或 passphrase 是否正确。", ex);
        }
    }

    // ── Host Key 两段式校验 ──────────────────────────────────────

    /// <summary>
    /// SSH.NET 在握手线程上同步触发。这里只做本地指纹比对：一致放行；
    /// 未信任即中止（e.CanTrust=false，凭据尚未发送），把待确认密钥记下，
    /// 由 ConnectAsync 失败路径弹窗确认后重试。绝不阻塞等待 UI。
    /// </summary>
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        try
        {
            var key = _options.HostKeyPolicy.Lookup(
                _options.Host, _options.Port, e.HostKeyName, e.FingerPrintSHA256);

            if (key.IsKnownGood)
            {
                e.CanTrust = true;
                return;
            }

            _logger.LogInformation(
                "SSH 会话 {SessionId} 主机密钥未信任，中止握手待确认，IsMismatch={IsMismatch}",
                SessionId, key.IsMismatch);
            _pendingHostKey = key;
            e.CanTrust = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH 会话 {SessionId} 查询主机密钥异常", SessionId);
            _hostKeyFailure = SshConnectionErrorCode.HostKeyRejected;
            e.CanTrust = false;
        }
    }

    // ── 读取与清理 ───────────────────────────────────────────────

    private async Task ReadLoopAsync(ShellStream shell, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(buffer.AsMemory(), ct);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                DataReceived?.Invoke(this, chunk);
            }

            if (!ct.IsCancellationRequested && !_closing && !_disposed && State == SshSessionState.Connected)
            {
                _logger.LogInformation("SSH 会话 {SessionId} 被远端关闭", SessionId);
                SetState(SshSessionState.Disconnected);
                _closing = true;
                await CleanupAsync(skipReadLoopWait: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (State == SshSessionState.Connected && !_closing && !_disposed)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 读取数据中断", SessionId);
            Fail(SshConnectionErrorCode.RemoteClosed, ex);
        }
    }

    private async Task CleanupAsync(bool skipReadLoopWait = false)
    {
        try
        {
            await _lifecycleMutex.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_readLoopCts is not null)
            {
                await _readLoopCts.CancelAsync();
            }

            if (!skipReadLoopWait && _readLoopTask is not null)
            {
                var readLoop = _readLoopTask;
                if (!ReferenceEquals(await Task.WhenAny(readLoop, Task.Delay(TimeSpan.FromSeconds(2))), readLoop))
                {
                    _logger.LogWarning("SSH 会话 {SessionId} 读取循环在清理时未及时退出", SessionId);
                }
            }
            _readLoopTask = null;
            _readLoopCts?.Dispose();
            _readLoopCts = null;

            _shell?.Dispose();
            _shell = null;

            if (_client is not null)
            {
                _client.HostKeyReceived -= OnHostKeyReceived;
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
                _client.Dispose();
                _client = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH 会话 {SessionId} 清理资源异常", SessionId);
        }
        finally
        {
            try
            {
                _lifecycleMutex.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void CancelLifecycle()
    {
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Fail(SshConnectionErrorCode code, Exception? ex)
    {
        ErrorCode = code;
        ErrorMessage = SshConnectionErrorText.Describe(code);
        _logger.LogError(ex, "SSH 会话 {SessionId} 连接失败，错误码 {ErrorCode}", SessionId, code);
        SetState(SshSessionState.Failed);
    }

    private void SetState(SshSessionState newState)
    {
        if (State == newState)
        {
            return;
        }

        State = newState;
        StateChanged?.Invoke(this, newState);
    }
}
