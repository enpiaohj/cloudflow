using System.IO;
using Microsoft.Web.WebView2.Core;

namespace CloudFlow.Terminal.Terminal;

/// <summary>
/// 进程内共享的 WebView2 环境：所有终端窗口复用同一套浏览器子进程，
/// 多窗口不再各自拉起 msedgewebview2 进程（参考 RemoteFlow SharedWebView2Environment）。
/// </summary>
public static class SharedWebView2Environment
{
    private static SemaphoreSlim _mutex = new(1, 1);
    private static CoreWebView2Environment? _environment;
    private static int _refCount;

    /// <summary>取得共享环境引用。用完必须调用 <see cref="Release"/> 归还。</summary>
    public static async Task<CoreWebView2Environment> AcquireAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (_environment is null)
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudFlow", "WebView2");
                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);
            }

            _refCount++;
            return _environment;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public static void Release()
    {
        _refCount--;
    }
}
