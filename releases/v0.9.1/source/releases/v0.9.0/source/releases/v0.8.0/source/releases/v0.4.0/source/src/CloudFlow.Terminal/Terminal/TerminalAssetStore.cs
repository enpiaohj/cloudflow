using System.IO;

namespace CloudFlow.Terminal.Terminal;

/// <summary>
/// 终端前端资产（terminal.html + xterm.js 等）的磁盘位置。
/// 资产随项目以 Content 方式拷到输出目录 Terminal/Assets。
/// </summary>
public static class TerminalAssetStore
{
    /// <summary>资产目录不存在或缺关键文件时抛出，避免运行中才发现 404。</summary>
    public static string EnsureAvailable()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Terminal", "Assets");
        if (!File.Exists(Path.Combine(directory, "terminal.html")))
        {
            throw new InvalidOperationException(
                $"终端资源缺失：{directory}。请重新安装应用（Terminal/Assets 目录必须随程序一起发布）。");
        }

        return directory;
    }
}
