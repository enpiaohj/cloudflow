using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 设置页「凭据管理」分节与连接框的静态一致性检查。
///
/// **为什么这两条值得单独存在**（它们各自对应一类"编译能过、运行才炸"的失败）：
/// <list type="number">
/// <item>
/// <b>分节 key 两侧不一致</b>：<c>SettingsViewModel.AllSections</c> 与 <c>SettingsPage.xaml</c> 里
/// 每个卡片的 <c>ConverterParameter</c> 是两份手写清单。写岔一个字母的后果是
/// 「点了左侧分节，右侧一片空白」—— 不报错、不崩溃，页面上就是什么都没发生。
/// </item>
/// <item>
/// <b>绑定路径写错</b>：WPF 绑定失败是<b>静默</b>的（只往输出窗口写一行 Trace）。
/// 把 <c>CredentialMessageSeverity</c> 敲成 <c>CredentialMessageSevrity</c>，
/// 结果是提示条的配色永远走不到、文案却照常显示 —— 看截图根本看不出异样。
/// </item>
/// </list>
///
/// 本项目不引用 CloudFlow.App（见 csproj），因此这里做的是<b>源码文本层面</b>的检查：
/// 断言绑定路径的首段确实出现在 ViewModel 的成员名列里。<b>它只能证明"这个名字存在"，
/// 不能证明"这条绑定挂在与它匹配的那个类型上"</b> —— 后者要真跑应用才看得见，是已知缺口，不掩饰。
/// </summary>
public sealed class CredentialUiStaticTests
{
    /// <summary>
    /// 分节 key 的<b>唯一真源</b>是 <c>SettingsViewModel</c> 的 <c>AllSections</c>；
    /// 视图里的 <c>ConverterParameter</c> 是它的镜像。两者必须逐一对应。
    /// </summary>
    [Fact]
    public void 设置页分节key必须与AllSections一一对应()
    {
        var declared = SectionKeysInViewModel();
        var mirrored = SectionKeysInXaml();

        Assert.True(declared.Count > 0, "未从 SettingsViewModel 解析出任何分节 key，扫描逻辑大概率失效了。");

        Assert.Equal(
            declared.OrderBy(key => key, StringComparer.Ordinal),
            mirrored.OrderBy(key => key, StringComparer.Ordinal));
    }

    /// <summary>
    /// 凭据卡片里每个 <c>{Binding …}</c> 的首段，必须在 <c>SettingsViewModel</c>
    /// 或 <c>CredentialRowViewModel</c> 上真实存在。
    /// </summary>
    [Fact]
    public void 凭据卡片里的绑定路径必须在ViewModel上存在()
    {
        var card = CredentialCard();
        var members = ViewModelMembers();

        // ── 反向保护：先证明"成员收集器"和"扫描"都真的工作 ──
        // 少一条，下面的"零问题"就可能只是空集造成的假通过。
        Assert.Contains("HasNoCredentials", members);       // 显式属性
        Assert.Contains("Credentials", members);            // [ObservableProperty] 生成的属性
        Assert.Contains("NewCredentialCommand", members);   // [RelayCommand] 生成的命令
        Assert.Contains("AuthTypeKey", members);            // 行 VM 的属性
        Assert.DoesNotContain("AuthTypeKe", members);       // 精确匹配，不是前缀匹配

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (owner, expression) in Bindings(card))
        {
            var first = FirstSegment(expression);
            if (first is null)
            {
                continue;   // {Binding} 整对象绑定，没有路径
            }

            seen.Add(first);
            if (!members.Contains(first))
            {
                problems.Add($"{owner} → {first}");
            }
        }

        Assert.True(seen.Count >= 10, $"只扫到 {seen.Count} 个绑定首段，扫描逻辑大概率失效了。");
        Assert.True(problems.Count == 0,
            "以下绑定路径的首段在 SettingsViewModel / CredentialRowViewModel 上都不存在。" +
            "WPF 绑定失败不会报错，只会让那一处永远取不到值：" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// 连接框下拉的"新建/临时输入"入口必须与设置页空态文案里写的入口名一致。
    ///
    /// 空态文案是用户唯一能读到的指引；它指向一个界面上不存在的标签，
    /// 用户就会去找一个没有的按钮。这类不一致没有任何编译期保护。
    /// </summary>
    [Fact]
    public void 设置页空态文案指向的连接框入口必须真的存在()
    {
        var dialog = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SshCredentialDialog.xaml.cs"));
        var label = TransientLabelPattern.Match(dialog);
        Assert.True(label.Success, "未从 SshCredentialDialog.xaml.cs 解析出下拉首项的文案常量。");

        var labelText = label.Groups[1].Value;
        var page = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"));

        Assert.True(page.Contains(labelText, StringComparison.Ordinal),
            $"设置页空态文案里没有出现连接框下拉首项的文案「{labelText}」，指引会指向一个不存在的入口。");
    }

    // ── 成员收集 ──────────────────────────────────────────────────

    /// <summary>匹配 <c>[ObservableProperty]</c> 之后那个私有字段，取其后缀作为生成的属性名。</summary>
    private static readonly Regex ObservableField = new(
        @"\[ObservableProperty\][\s\S]{0,400}?private\s[^;]*?\b_([A-Za-z0-9]+)\s*(?:=|;)",
        RegexOptions.Compiled);

    /// <summary>匹配 <c>[RelayCommand]</c> 标注的方法名，用于推导生成的 <c>&lt;Name&gt;Command</c>。</summary>
    private static readonly Regex RelayCommandMethod = new(
        @"\[RelayCommand[^\]]*\][\s\S]{0,400}?private\s[^;={]*?\b([A-Z][A-Za-z0-9]*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>公开成员（属性 / 表达式属性 / 字段）。</summary>
    private static readonly Regex PublicMember = new(
        @"public\s+[A-Za-z0-9_<>?\[\],\.\s]+?\s([A-Z][A-Za-z0-9]*)\s*(?:=>|\{|;)",
        RegexOptions.Compiled);

    private static readonly Regex TransientLabelPattern = new(
        @"TransientLabel\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex SectionsBlock = new(@"AllSections\s*=\s*\[([\s\S]*?)\];", RegexOptions.Compiled);

    private static readonly Regex SectionEntry = new(@"new\(\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex SectionParameter = new(
        @"Converter=\{StaticResource\s+SectionVisible\}\s*,\s*ConverterParameter=([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static HashSet<string> ViewModelMembers()
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in new[] { "SettingsViewModel.cs", "CredentialRowViewModel.cs" })
        {
            var source = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", file));

            foreach (Match match in PublicMember.Matches(source))
            {
                members.Add(match.Groups[1].Value);
            }

            // [ObservableProperty] private T _foo;  → 生成属性 Foo
            foreach (Match match in ObservableField.Matches(source))
            {
                members.Add(PascalCase(match.Groups[1].Value));
            }

            // [RelayCommand] private ... FooAsync() → 生成 FooCommand
            foreach (Match match in RelayCommandMethod.Matches(source))
            {
                var name = match.Groups[1].Value;
                if (name.EndsWith("Async", StringComparison.Ordinal))
                {
                    name = name[..^"Async".Length];
                }

                members.Add(name + "Command");
            }
        }

        return members;
    }

    private static string PascalCase(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static List<string> SectionKeysInViewModel()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "SettingsViewModel.cs"));
        var block = SectionsBlock.Match(source);

        Assert.True(block.Success, "SettingsViewModel 里未找到 AllSections 数组初始化块，扫描逻辑失效。");

        return [.. SectionEntry.Matches(block.Groups[1].Value).Select(match => match.Groups[1].Value)];
    }

    private static SortedSet<string> SectionKeysInXaml()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"));
        return [.. SectionParameter.Matches(source).Select(match => match.Groups[1].Value)];
    }

    /// <summary>凭据卡片：以 <c>ConverterParameter=credentials</c> 作为语义锚点，而不是行号。</summary>
    private static XElement CredentialCard() =>
        XDocument.Load(Path.Combine(AppDirectory(), "Views", "SettingsPage.xaml"))
            .Descendants()
            .FirstOrDefault(element => element.Attributes().Any(attribute =>
                attribute.Value.Contains("ConverterParameter=credentials", StringComparison.Ordinal)))
        ?? throw new InvalidOperationException("SettingsPage.xaml 里未找到凭据管理分节卡片。");

    /// <summary>元素自身及后代的全部 <c>{Binding …}</c>；返回 (所属元素.属性, 原始表达式)。</summary>
    private static IEnumerable<(string Owner, string Expression)> Bindings(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Value.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    yield return ($"{element.Name.LocalName}.{attribute.Name.LocalName}", attribute.Value);
                }
            }
        }
    }

    /// <summary>
    /// 取绑定表达式的首段路径。<c>DataContext.X</c> 这种以 WPF 自身属性开头的写法
    /// （配合 RelativeSource 回绑上层 DataContext）会被剥掉前缀 ——
    /// <c>DataContext</c> 不是 ViewModel 的成员，拿它去比对只会得到假失败。
    /// </summary>
    private static string? FirstSegment(string bindingExpression)
    {
        var body = bindingExpression["{Binding".Length..].Trim().TrimEnd('}').Trim();

        var comma = body.IndexOf(',');
        var head = (comma < 0 ? body : body[..comma]).Trim();

        if (head.StartsWith("Path=", StringComparison.Ordinal))
        {
            head = head["Path=".Length..].Trim();
        }

        if (head.Length == 0)
        {
            return null;
        }

        var parts = head.Split('.');
        var first = parts[0].Split('[')[0].Trim();

        if (first != "DataContext")
        {
            return first.Length == 0 ? null : first;
        }

        return parts.Length > 1 ? parts[1].Split('[')[0].Trim() : null;
    }

    private static string AppDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "CloudFlow.App");

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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位凭据界面文件。");
    }
}
