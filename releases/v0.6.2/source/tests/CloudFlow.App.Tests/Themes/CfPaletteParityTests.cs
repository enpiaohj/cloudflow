using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CloudFlow.App.Tests.Themes;

/// <summary>
/// 浅色 / 深色两套调色板的一致性检查。
///
/// **为什么这条检查值得单独存在**：切到深色后，漏掉的那个 key 表现为"某个控件还是浅色"。
/// 页面上有 5 个页面 + 5 个对话框、几十处画刷引用，肉眼逐个核对一次要很久，
/// 而且漏掉一个（比如只在设备码遮罩里用的 <c>Cf.Surface.DangerBrush</c>）
/// 要等到那条错误分支真的出现才会被发现。
///
/// 这里**不加载 WPF**：两个文件是纯 XML，用 <see cref="XDocument"/> 解析即可，
/// 于是这个项目不需要 net8.0-windows、不需要 STA、不需要引用 CloudFlow.App。
/// </summary>
public sealed class CfPaletteParityTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XName KeyAttribute = XName.Get("Key", XamlNamespace);
    [Fact]
    public void 深浅两套调色板的key集合必须完全一致()
    {
        var light = Keys("CfPalette.Light.xaml");
        var dark = Keys("CfPalette.Dark.xaml");

        var missingInDark = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missingInLight = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(
            missingInDark.Count == 0 && missingInLight.Count == 0,
            $"两套调色板的 key 不一致。{Environment.NewLine}" +
            $"深色缺少：{(missingInDark.Count == 0 ? "（无）" : string.Join("、", missingInDark))}{Environment.NewLine}" +
            $"深色多出：{(missingInLight.Count == 0 ? "（无）" : string.Join("、", missingInLight))}");
    }

    /// <summary>
    /// 状态徽章的 6 个档位在调色板里必须各有背景与前景。
    /// 缺一个不会崩：CfStatusBrushConverter 会退回占位色，页面上只是某个徽章颜色不对 ——
    /// 而那要等对应状态真的出现才看得见（比如只有 VM 被解除分配时才会渲染的"已解除分配"徽章）。
    /// </summary>
    [Fact]
    public void 调色板必须覆盖状态徽章的全部档位()
    {
        var keys = Keys("CfPalette.Light.xaml");
        var expected = new[] { "Success", "Warning", "Danger", "Neutral", "Info", "Subnet" }
            .SelectMany(tone => new[] { $"Cf.Status.{tone}.Bg", $"Cf.Status.{tone}.Fg" })
            .Where(key => !keys.Contains(key))
            .ToList();

        Assert.True(expected.Count == 0,
            $"调色板缺少状态档位：{string.Join("、", expected)}");
    }

    /// <summary>
    /// XAML 里引用的每个 <c>Cf.*</c> key 都必须真的有定义。
    ///
    /// 拼错一个 key（<c>Cf.CardBrsh</c>）不会报错：<c>DynamicResource</c> 找不到就静默不赋值，
    /// 表现是"这个控件的背景/文字色没了"，而且只在那一处。这条断言把拼写错误挡在编译后。
    ///
    /// **"有定义"包含页面自己的局部资源**：<c>Cf.</c> 这个前缀被当成"主题资源"用了，
    /// 但实际还有 5 个同名风格非主题资源 —— <c>VirtualMachinesPage.xaml</c> 的
    /// <c>Cf.VmRowMenu</c>、<c>VmDetailPage.xaml</c> 的 <c>Cf.RuleRowMenu</c> /
    /// <c>Cf.DiskRowMenu</c> / <c>Cf.RuleActionCell</c> / <c>Cf.RuleOriginCell</c>，
    /// 它们定义在各自页面的 <c>UserControl.Resources</c> 里。只看主题字典会把这 5 个
    /// 报成"未定义"，所以定义集合要把**被扫描文件自身**的 key 也算进去。
    /// </summary>
    [Fact]
    public void XAML里引用的每个Cf资源都必须有定义()
    {
        var theme = DefinedKeys("CfPalette.Light.xaml")
            .Concat(DefinedKeys("CloudFlowTheme.xaml"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories))
        {
            // 局部资源先并入：同文件里定义的 key 优先于主题，页面允许覆盖主题 key
            var defined = new HashSet<string>(theme, StringComparer.Ordinal);
            defined.UnionWith(LocalKeys(file));

            foreach (Match match in ResourceReference.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;
                if (!defined.Contains(key))
                {
                    missing.Add($"{key}（{Path.GetFileName(file)}）");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "以下 Cf.* 资源被引用但没有定义：" + string.Join("、", missing));
    }

    [Theory]
    [InlineData("CfPalette.Light.xaml")]
    [InlineData("CfPalette.Dark.xaml")]
    public void 每个画刷的颜色必须是合法的ARGB字面量(string file)
    {
        // 颜色值写错（少一位、写成颜色名、写成 #RGB）在编译期不报错，
        // 要到运行期 XAML 解析时才抛，且抛在 OnStartup 里 —— 应用直接起不来。
        var bad = new List<string>();
        foreach (var element in Elements(file))
        {
            var key = (string?)element.Attribute(KeyAttribute) ?? "(无 key)";
            var color = element.Attribute("Color")?.Value;
            if (color is null || !IsArgb(color))
            {
                bad.Add($"{key}={(color ?? "(无 Color)")}");
            }
        }

        Assert.True(bad.Count == 0,
            $"{file} 里以下画刷的颜色不是合法的 #AARRGGBB 字面量：{string.Join("、", bad)}");
    }

    /// <summary>匹配 <c>{DynamicResource Cf.xxx}</c> / <c>{StaticResource Cf.xxx}</c> 里的 key。</summary>
    private static readonly Regex ResourceReference =
        new(@"\{(?:Dynamic|Static)Resource\s+(Cf\.[A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    private static bool IsArgb(string value) =>
        value.Length == 9 &&
        value[0] == '#' &&
        value.AsSpan(1).ToString().All(Uri.IsHexDigit);

    /// <summary>文件里所有层级的 <c>x:Key</c>（主题文件里的资源可能嵌在 Style / DataTemplate 内）。</summary>
    private static IEnumerable<string> LocalKeys(string file) =>
        XDocument.Load(file).Descendants()
            .Select(e => (string?)e.Attribute(KeyAttribute))
            .Where(k => k is not null)
            .Select(k => k!);

    private static IEnumerable<string> DefinedKeys(string file) =>
        XDocument.Load(Path.Combine(ThemesDirectory(), file)).Root!
            .DescendantsAndSelf()
            .Select(e => (string?)e.Attribute(KeyAttribute))
            .Where(k => k is not null)
            .Select(k => k!);

    private static SortedSet<string> Keys(string file) =>
        [.. Elements(file)
            .Select(e => (string?)e.Attribute(KeyAttribute))
            .Where(k => k is not null)
            .Select(k => k!)];

    private static IEnumerable<XElement> Elements(string file) =>
        XDocument.Load(Path.Combine(ThemesDirectory(), file)).Root!.Elements();

    private static string ThemesDirectory() =>
        Path.Combine(AppDirectory(), "Themes");

    private static string AppDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "CloudFlow.App");

    /// <summary>
    /// 从测试程序集所在目录向上找到含 CloudFlow.sln 的目录。
    /// 不用 [CallerFilePath]：那会把**构建机器的绝对路径**编进测试程序集里。
    /// </summary>
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位调色板文件。");
    }
}
