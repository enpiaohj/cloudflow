using System.IO;
using CloudFlow.Terminal.Terminal;
using Xunit;

namespace CloudFlow.Terminal.Tests.Terminal;

/// <summary>
/// 终端资产定位的三条路：程序旁目录优先 → 内嵌资源释放兜底 → 都没有则抛可行动异常。
///
/// **回归背景**：单文件发布（PublishSingleFile）不打包 Content 文件，发布包里只有 exe、
/// 没有 Terminal/Assets——开发构建一直有输出目录兜底从未暴露，用户拿到的发布版
/// 终端渲染层起不来，所有页面的 SSH 连接都发不起来（曾表现为永远停在「待连接」）。
/// 修复后资产同时内嵌进程序集，运行时释放到缓存目录，发布版自包含。
/// </summary>
public sealed class TerminalAssetStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "cfterminal-assets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void 程序旁有资产目录时直接使用_不落缓存()
    {
        var baseDir = Path.Combine(_tempRoot, "app");
        Directory.CreateDirectory(Path.Combine(baseDir, "Terminal", "Assets"));
        File.WriteAllText(Path.Combine(baseDir, "Terminal", "Assets", "terminal.html"), "<html></html>");

        var resolved = TerminalAssetStore.EnsureAvailable(baseDir);

        Assert.Equal(Path.Combine(baseDir, "Terminal", "Assets"), resolved);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "cache", "Terminal", "Assets")));
    }

    [Fact]
    public void 程序旁没有资产时从内嵌资源释放到注入的缓存目录()
    {
        var baseDir = Path.Combine(_tempRoot, "app"); // 故意不创建 Terminal/Assets
        var cacheRoot = Path.Combine(_tempRoot, "cache");

        var resolved = TerminalAssetStore.EnsureAvailable(baseDir, cacheRoot);

        // 发布版的真实路径：exe 旁边没有 Terminal/Assets，从内嵌资源释放后可用
        Assert.Equal(Path.Combine(cacheRoot, "Terminal", "Assets"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "terminal.html")),
            "释放目录中应有 terminal.html。");
    }

    [Fact]
    public void 内嵌释放_全部资产落到缓存目录且内容非空()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");

        var resolved = TerminalAssetStore.EnsureExtracted(cacheRoot);

        Assert.Equal(Path.Combine(cacheRoot, "Terminal", "Assets"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "terminal.html")));
        Assert.True(File.Exists(Path.Combine(resolved, "xterm.js")));
        Assert.True(new FileInfo(Path.Combine(resolved, "xterm.js")).Length > 10_000,
            "xterm.js 是几百 KB 的构建产物，太小说明释放不完整。");
    }

    [Fact]
    public void 释放是幂等的_删掉文件后再调用会补齐()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");
        var resolved = TerminalAssetStore.EnsureExtracted(cacheRoot);

        File.Delete(Path.Combine(resolved, "terminal.html"));
        Assert.Equal(resolved, TerminalAssetStore.EnsureExtracted(cacheRoot));
        Assert.True(File.Exists(Path.Combine(resolved, "terminal.html")));
    }
}
