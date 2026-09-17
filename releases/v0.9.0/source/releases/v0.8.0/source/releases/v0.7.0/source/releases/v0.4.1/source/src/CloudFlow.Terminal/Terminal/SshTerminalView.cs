using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudFlow.Terminal.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CloudFlow.Terminal.Terminal;

/// <summary>
/// SSH 终端视图：WebView2 承载 xterm.js 作为渲染层（参考 RemoteFlow SshSessionView）。
/// <para>
/// 不自绘终端：ANSI 转义、东亚宽字符、滚动缓冲这些正确实现成本极高，
/// xterm.js 是被 VS Code 等长期验证的方案。协议层与渲染层只交换原始字节。
/// </para>
/// </summary>
public sealed class SshTerminalView : ContentControl, IDisposable
{
    /// <summary>WebView2 通过它以 https 方式加载本地终端资产，避免 file:// 的安全限制。</summary>
    private const string VirtualHost = "cloudflow.terminal";

    /// <summary>输出合批间隔：远端刷屏时逐包调 JS 开销大，按帧合并写入。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    private readonly Ssh.SshSession _session;
    private readonly ILogger _logger;

    private readonly WebView2 _webView;
    private readonly DispatcherTimer _flushTimer;

    private readonly List<byte[]> _pendingOutput = [];
    private readonly object _outputLock = new();

    private bool _terminalReady;
    private bool _connectStarted;
    private bool _disposed;
    private bool _holdsSharedEnvironment;

    /// <summary>窗口尺寸就绪后宿主应发起 SSH 连接（由 TerminalWindow 订阅）。</summary>
    public event EventHandler? TerminalReady;

    public SshTerminalView(Ssh.SshSession session, ILogger logger)
    {
        _session = session;
        _logger = logger;

        _webView = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30)
        };
        Content = _webView;

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += OnFlushTick;

        _session.DataReceived += OnSessionDataReceived;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化终端失败");
            SetStateLine("终端组件初始化失败。请确认本机已安装 Microsoft Edge WebView2 运行时。");
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var environment = await SharedWebView2Environment.AcquireAsync();
        _holdsSharedEnvironment = true;

        if (_disposed)
        {
            SharedWebView2Environment.Release();
            return;
        }

        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;

        // 终端是本地 UI，不需要浏览器能力，一律关闭以缩小攻击面。
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        var assetFolder = TerminalAssetStore.EnsureAvailable();
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, assetFolder, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;

        _webView.Source = new Uri($"https://{VirtualHost}/terminal.html");
    }

    // ── 与终端前端的消息往来 ──────────────────────────────────────

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // 前端 postMessage(JSON.stringify(...))，取字符串解析而非 WebMessageAsJson（会二次编码）。
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    await OnTerminalReadyAsync(root);
                    break;

                case "input":
                    var payload = root.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(payload))
                    {
                        _session.SendInput(Convert.FromBase64String(payload));
                    }
                    break;

                case "resize":
                    _session.Resize(
                        root.GetProperty("cols").GetUInt32(),
                        root.GetProperty("rows").GetUInt32(),
                        root.GetProperty("width").GetUInt32(),
                        root.GetProperty("height").GetUInt32());
                    break;

                case "paste-intercepted":
                    // 大文本粘贴由前端拦截后转交；直接作为输入送往远端
                    var pasteText = root.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(pasteText))
                    {
                        _session.SendInput(Encoding.UTF8.GetBytes(pasteText));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理终端消息失败");
        }
    }

    /// <summary>
    /// 终端就绪后才发起 SSH 连接：行列数已确定，远端从一开始就按正确尺寸绘制，
    /// 避免 vim/top 之类全屏程序错位。
    /// </summary>
    private async Task OnTerminalReadyAsync(JsonElement message)
    {
        _terminalReady = true;
        _flushTimer.Start();

        await InvokeTerminalAsync("setTheme", "dark", true);

        TerminalReady?.Invoke(this, EventArgs.Empty);

        await _session.ConnectAsync();

        if (_session.State == SshSessionState.Connected)
        {
            _session.Resize(
                message.GetProperty("cols").GetUInt32(),
                message.GetProperty("rows").GetUInt32(),
                (uint)ActualWidth,
                (uint)ActualHeight);
            await InvokeTerminalAsync("focus");
        }
        else if (_session.ErrorMessage is { } error)
        {
            // 失败原因写进终端本体，用户在会话里就能看到发生了什么
            await InvokeTerminalAsync("writeLine", $"[31m{error}[0m");
        }
    }

    /// <summary>在终端里写一行本地提示（白色）。</summary>
    public async Task WriteLocalLineAsync(string text)
    {
        await InvokeTerminalAsync("writeLine", text);
    }

    // ── 输出合批 ──────────────────────────────────────────────────

    private void OnSessionDataReceived(object? sender, byte[] data)
    {
        // 来自 SSH 读取线程：只入队，真正写入由 UI 线程按帧完成。
        lock (_outputLock)
        {
            _pendingOutput.Add(data);
        }
    }

    private async void OnFlushTick(object? sender, EventArgs e)
    {
        if (_disposed || !_terminalReady)
        {
            return;
        }

        byte[] batch;
        lock (_outputLock)
        {
            if (_pendingOutput.Count == 0)
            {
                return;
            }

            var total = _pendingOutput.Sum(chunk => chunk.Length);
            batch = new byte[total];
            var offset = 0;
            foreach (var chunk in _pendingOutput)
            {
                Buffer.BlockCopy(chunk, 0, batch, offset, chunk.Length);
                offset += chunk.Length;
            }
            _pendingOutput.Clear();
        }

        await InvokeTerminalAsync("write", Convert.ToBase64String(batch));
    }

    private async Task InvokeTerminalAsync(string method, params object[] args)
    {
        if (_disposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        var serialized = string.Join(", ", args.Select(arg => JsonSerializer.Serialize(arg)));
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.rfTerminal.{method}({serialized})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调用终端方法 {Method} 失败", method);
        }
    }

    private void SetStateLine(string text)
    {
        _ = WriteLocalLineAsync(text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Stop();
        _session.DataReceived -= OnSessionDataReceived;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        if (_holdsSharedEnvironment)
        {
            SharedWebView2Environment.Release();
        }

        _webView.Dispose();
    }
}
