using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// XAML 里 <c>Symbol="Xxx24"</c> 这类枚举字面量的取值校验。
///
/// 为什么需要它：<c>ui:SymbolIcon.Symbol</c> 是枚举属性，XAML 用 <c>EnumConverter</c> 在**运行时**转换，
/// 编译器只把字符串原样写进 BAML，不校验枚举成员。写错一个名字（例如不存在的 <c>Terminal24</c>）
/// 不会编译失败，而是等到该页面第一次布局时才抛 <see cref="System.Windows.Markup.XamlParseException"/> ——
/// 实测这台机器上就是应用启动即崩，日志只有一句 "Terminal24 is not a valid value for SymbolRegular"。
///
/// 本测试把仓库里每个 XAML 字面量与 WPF-UI 包里 <c>SymbolRegular</c> 的真实成员表比对，
/// 让这类拼写错误在编译后的测试阶段就暴露。C# 侧的 <c>SymbolRegular.Xxx</c> 是编译期检查的，不在此列。
/// </summary>
public sealed class XamlSymbolLiteralTests
{
    /// <summary>匹配 <c>Symbol="..."</c>；值以 <c>{</c> 开头的是标记扩展（如 {Binding Symbol}），不是枚举字面量。</summary>
    private static readonly Regex SymbolAttributePattern = new("Symbol\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    [Fact]
    public void 所有XAML中的Symbol字面量必须是SymbolRegular的合法成员()
    {
        var valid = SymbolRegularNames();
        var problems = new List<string>();

        foreach (var file in XamlFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in SymbolAttributePattern.Matches(lines[index]))
                {
                    var value = match.Groups[1].Value;

                    // 标记扩展（{Binding Symbol} / {x:Static ...}）交由运行时解析，不是字面量
                    if (value.StartsWith('{'))
                    {
                        continue;
                    }

                    if (!valid.Contains(value))
                    {
                        problems.Add($"{Relative(file)}:{index + 1}  Symbol=\"{value}\"");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0,
            "以下 Symbol 字面量不是 WPF-UI SymbolRegular 的成员，运行到该页面时会抛 XamlParseException：" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// 反向保护：这条扫描必须真的扫到了文件与字面量，否则"零问题"毫无意义
    /// （路径写错、正则写错都会让上面的断言空过）。
    /// </summary>
    [Fact]
    public void Symbol扫描必须覆盖到页面文件与字面量()
    {
        var files = XamlFiles().ToList();
        Assert.Contains(files, file => file.EndsWith(Path.Combine("Views", "VmDetailPage.xaml"), StringComparison.Ordinal));

        var literalCount = files
            .SelectMany(File.ReadAllLines)
            .SelectMany(line => SymbolAttributePattern.Matches(line).Select(match => match.Groups[1].Value))
            .Count(value => !value.StartsWith('{'));
        Assert.True(literalCount > 0, "未扫描到任何 Symbol 字面量，扫描逻辑大概率失效了。");
    }

    private static HashSet<string> SymbolRegularNames()
    {
        var assemblyPath = ResolveWpfUiAssembly();

        // 只反射取枚举成员，不触碰任何 WPF 类型，因此不需要 WindowsDesktop 框架引用
        var type = Assembly.LoadFrom(assemblyPath).GetType("Wpf.Ui.Controls.SymbolRegular")
                   ?? throw new InvalidOperationException($"{assemblyPath} 中未找到 Wpf.Ui.Controls.SymbolRegular。");

        return [.. Enum.GetNames(type)];
    }

    /// <summary>
    /// 从 CloudFlow.App.csproj 里读 WPF-UI 的固定版本，再到 NuGet 全局包目录定位同名程序集 ——
    /// 让校验对象与项目实际引用的版本始终一致，而不是硬编码某个版本的路径。
    /// </summary>
    private static string ResolveWpfUiAssembly()
    {
        var projectFile = Path.Combine(RepositoryRoot(), "src", "CloudFlow.App", "CloudFlow.App.csproj");
        var version = XDocument.Load(projectFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Where(element => string.Equals((string?)element.Attribute("Include"), "WPF-UI", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("Version"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidOperationException("CloudFlow.App.csproj 中未找到 WPF-UI 的 PackageReference。");

        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        var libRoot = Path.Combine(packagesRoot, "wpf-ui", version, "lib");
        if (!Directory.Exists(libRoot))
        {
            throw new InvalidOperationException(
                $"未找到 WPF-UI {version} 的包目录：{libRoot}。若本机改过 NUGET_PACKAGES，请设置同名环境变量。");
        }

        // 目标框架目录里选 net8.0-windows* 优先，其次任一含 Wpf.Ui.dll 的目录
        var candidates = new List<string> { Path.Combine(libRoot, "net8.0-windows7.0", "Wpf.Ui.dll") };
        candidates.AddRange(Directory.EnumerateDirectories(libRoot)
            .OrderByDescending(directory => Path.GetFileName(directory).StartsWith("net8.0", StringComparison.Ordinal))
            .Select(directory => Path.Combine(directory, "Wpf.Ui.dll")));

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new InvalidOperationException($"{libRoot} 下未找到 Wpf.Ui.dll。");
    }

    private static IEnumerable<string> XamlFiles()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位 XAML 文件。");
    }
}
