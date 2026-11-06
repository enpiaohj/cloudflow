using System.IO;
using System.Reflection;

namespace CloudFlow.Terminal.Terminal;

/// <summary>
/// 终端前端资产（terminal.html + xterm.js 等）的磁盘位置。
///
/// 真实踩到的坑：单文件发布（PublishSingleFile）**不会打包 Content 文件**——发布输出里
/// `Terminal/Assets` 是散落在 exe 旁边的，打包时只带走了 exe 本身，用户拿到的 EXE 就
/// 没有这批资产，终端渲染层起不来，所有页面的 SSH 连接都发不起来（开发构建一直有输出
/// 目录兜底，所以从未暴露）。因此资产同时以 EmbeddedResource 随程序集走：
/// <list type="bullet">
/// <item>程序旁有 `Terminal/Assets`（开发构建 / 显式分发）→ 直接用，不落缓存；</item>
/// <item>没有 → 从内嵌资源释放到 `%LOCALAPPDATA%\CloudFlow\Terminal\Assets` 并返回该目录。</item>
/// </list>
/// </summary>
public static class TerminalAssetStore
{
    /// <summary>内嵌资源的公共段。按这一段过滤而不是写死完整命名空间，避免命名空间漂移后静默失配。</summary>
    private const string ResourceMarker = ".Terminal.Assets.";

    public static string EnsureAvailable() => EnsureAvailable(AppContext.BaseDirectory);

    /// <exception cref="InvalidOperationException">本地目录与内嵌资源两条路都拿不到资产时抛出，
    /// 避免运行中才发现 WebView2 加载 404。</exception>
    public static string EnsureAvailable(string baseDirectory, string? cacheRoot = null)
    {
        var directory = Path.Combine(baseDirectory, "Terminal", "Assets");
        if (File.Exists(Path.Combine(directory, "terminal.html")))
        {
            return directory;
        }

        return EnsureExtracted(cacheRoot);
    }

    /// <summary>把内嵌资产释放到用户缓存目录（幂等：内容没变就跳过），返回该目录。
    /// <paramref name="cacheRoot"/> 供测试注入临时目录；生产路径用默认的 %LOCALAPPDATA%。</summary>
    public static string EnsureExtracted(string? cacheRoot = null)
    {
        var directory = cacheRoot is not null
            ? Path.Combine(cacheRoot, "Terminal", "Assets")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudFlow", "Terminal", "Assets");

        try
        {
            var assembly = typeof(TerminalAssetStore).Assembly;
            var assets = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal))
                .ToList();

            if (assets.Count == 0)
            {
                throw new InvalidOperationException(
                    "程序集中未找到内嵌终端资源（Terminal/Assets），安装包可能不完整。请重新下载 CloudFlow。");
            }

            Directory.CreateDirectory(directory);
            foreach (var name in assets)
            {
                var fileName = name[(name.IndexOf(ResourceMarker, StringComparison.Ordinal)
                                     + ResourceMarker.Length)..];
                // 资产名可能把路径分隔符编进资源名，取末段保证落在同一层
                fileName = Path.GetFileName(fileName);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var target = Path.Combine(directory, fileName);
                using var stream = assembly.GetManifestResourceStream(name)!;
                if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
                {
                    continue; // 已是同尺寸的同名文件，跳过重写，避免每次启动都碰磁盘
                }

                using var output = File.Create(target);
                stream.CopyTo(output);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"终端资源释放失败（{directory}）：{ex.Message}", ex);
        }

        if (!File.Exists(Path.Combine(directory, "terminal.html")))
        {
            throw new InvalidOperationException(
                $"终端资源缺失：{directory}。请重新安装应用（Terminal/Assets 目录必须随程序一起发布）。");
        }

        return directory;
    }
}
