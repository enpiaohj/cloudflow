using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace CloudFlow.App.Tests.Layouts;

/// <summary>
/// 页面布局的静态约束。
///
/// 此项目不加载 WPF（见项目文件），但滚动条策略本身是 XAML 属性；直接解析源文件即可阻止
/// 「最小窗口宽度下表格已超出视口，却关闭横向滚动」这种运行时才看得见的回归。
/// </summary>
public sealed class VmListLayoutTests
{
    // MainWindow 的最小宽度 1040，固定侧栏 216，页面左右边距各 28 → 内容最窄 768。
    private const int MinimumContentWidth = 768;

    [Fact]
    public void 列宽超过最小视口时虚拟机表格必须允许水平滚动()
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "VirtualMachinesPage.xaml"));
        var grid = document.Descendants()
            .Single(element => element.Name.LocalName == "DataGrid" &&
                               element.Attributes().Any(attribute =>
                                   attribute.Name.LocalName == "Name" && attribute.Value == "VmsGrid"));

        Assert.NotEqual("Disabled", (string?)grid.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void 列宽超过网络面板可用宽度时规则表格必须允许水平滚动()
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "VmDetailPage.xaml"));
        var ruleTables = document.Descendants()
            .Where(element => element.Name.LocalName == "DataGrid")
            .Where(element =>
            {
                var itemsSource = (string?)element.Attribute("ItemsSource");
                return itemsSource is "{Binding InboundRules}" or "{Binding OutboundRules}";
            })
            .ToList();

        Assert.Equal(2, ruleTables.Count);
        Assert.All(ruleTables, table =>
            Assert.NotEqual("Disabled", (string?)table.Attribute("HorizontalScrollBarVisibility")));
    }

    [Fact]
    public void 最小窗口内筛选行必须保留刷新入口()
    {
        // 这里的 36px 是 Cf.IconButton 的刷新入口最小占位。
        const int refreshButtonWidth = 36;

        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "VirtualMachinesPage.xaml"));
        var filter = document.Descendants()
            .Single(element => element.Name.LocalName == "Grid" &&
                               element.Attributes().Any(attribute =>
                                   attribute.Name.LocalName == "Name" && attribute.Value == "FilterRow"));
        var firstColumnWidth = int.Parse(filter.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .First()
            .Attribute("Width")!.Value, CultureInfo.InvariantCulture);
        var comboBoxes = filter.Elements()
            .Where(element => element.Name.LocalName == "ComboBox")
            .Select(element => new
            {
                Width = int.Parse(element.Attribute("Width")!.Value, CultureInfo.InvariantCulture),
                LeftMargin = int.Parse(element.Attribute("Margin")!.Value.Split(',')[0], CultureInfo.InvariantCulture)
            })
            .ToList();

        var occupiedWidth = firstColumnWidth +
                            comboBoxes.Sum(comboBox => comboBox.Width + comboBox.LeftMargin) +
                            refreshButtonWidth;

        Assert.True(occupiedWidth <= MinimumContentWidth,
            $"筛选行固定控件需要 {occupiedWidth}px，但最小窗口只能提供 {MinimumContentWidth}px；刷新按钮会被挤出可视区。");
    }

    /// <summary>
    /// DataGrid 压缩列时的下限是每列的 MinWidth（不是 Width；固定宽度列默认可被压到 20px）。
    /// 只要全部列的 MinWidth 合计低于最小视口，窄窗口下列会被压碎（徽章截断、复选框变形）
    /// 而横向滚动条不出现 —— 实测：状态列 84px 在 1040 窗口下被压到约 65px。
    /// 所以固定宽度列的 MinWidth 必须 ≥ Width，且 MinWidth 合计必须超过最小视口，
    /// 让 DataGrid 在窄窗口下走"溢出 → 横向滚动条"而不是"按比例压碎列"。
    /// </summary>
    [Fact]
    public void 虚拟机表格固定宽度列必须有不可压缩下限且合计超过最小视口()
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "VirtualMachinesPage.xaml"));
        var grid = document.Descendants()
            .Single(element => element.Name.LocalName == "DataGrid" &&
                               element.Attributes().Any(attribute =>
                                   attribute.Name.LocalName == "Name" && attribute.Value == "VmsGrid"));
        var columns = grid.Descendants()
            .Where(element => element.Name.LocalName.StartsWith("DataGrid") &&
                              element.Name.LocalName.EndsWith("Column"))
            .ToList();

        var problems = new List<string>();
        var minWidthSum = 0d;
        foreach (var column in columns)
        {
            var width = ParseDataGridLength((string?)column.Attribute("Width"), out var isStar);
            var minWidth = ParseDataGridLength((string?)column.Attribute("MinWidth"), out _);

            if (isStar)
            {
                minWidthSum += minWidth;
                if (minWidth == 0)
                {
                    problems.Add($"星号列「{(string?)column.Attribute("Header") ?? "(未命名)"}」缺少 MinWidth");
                }
            }
            else
            {
                if (minWidth < width)
                {
                    problems.Add($"固定列「{(string?)column.Attribute("Header") ?? "(未命名)"}」Width={width} 但 MinWidth={minWidth}，窄窗口下会被压缩");
                }
                minWidthSum += Math.Max(width, minWidth);
            }
        }

        Assert.True(problems.Count == 0, string.Join("；", problems));
        Assert.True(minWidthSum > MinimumContentWidth,
            $"全部列 MinWidth 合计 {minWidthSum}px 未超过最小内容宽 {MinimumContentWidth}px，" +
            "窄窗口下 DataGrid 会压碎列而不是出现横向滚动条。");
    }

    /// <summary>解析 DataGridLength 字面量；"*" / "1.2*" 返回 0 并标记 isStar。</summary>
    private static double ParseDataGridLength(string? value, out bool isStar)
    {
        isStar = false;
        if (string.IsNullOrWhiteSpace(value) || value == "Auto" || value == "SizeToHeader" || value == "SizeToCells")
        {
            return 0;
        }

        if (value.EndsWith('*'))
        {
            isStar = true;
            return 0;
        }

        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 详情页网络面板的两张规则表与 VM 列表同因：固定宽度列（端口/协议/操作/生效于/⋯）
    /// 没有 MinWidth 时，窄窗口下被 DataGrid 压碎而不出横向滚动条（实测"端口 3000"被切成"300("）。
    /// </summary>
    [Theory]
    [InlineData("InboundRules")]
    [InlineData("OutboundRules")]
    public void 规则表格固定宽度列必须有不可压缩下限(string itemsSource)
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "VmDetailPage.xaml"));
        var table = document.Descendants()
            .Single(element => element.Name.LocalName == "DataGrid" &&
                               (string?)element.Attribute("ItemsSource") == $"{{Binding {itemsSource}}}");

        var problems = new List<string>();
        foreach (var column in table.Descendants()
                     .Where(element => element.Name.LocalName.StartsWith("DataGrid") &&
                                       element.Name.LocalName.EndsWith("Column")))
        {
            var width = ParseDataGridLength((string?)column.Attribute("Width"), out var isStar);
            var minWidth = ParseDataGridLength((string?)column.Attribute("MinWidth"), out _);
            if (isStar || minWidth >= width)
            {
                continue;
            }

            problems.Add(
                $"固定列「{(string?)column.Attribute("Header") ?? "(未命名)"}」Width={width} 但 MinWidth={minWidth}");
        }

        Assert.True(problems.Count == 0,
            $"{itemsSource} 表格存在会被窄窗口压碎的列：{string.Join("；", problems)}");
    }

    /// <summary>
    /// 任务表与 VM 列表同因：固定列没有 MinWidth 时窄窗口下被压碎
    /// （实测风险徽章被裁成空圆而不出滚动条）。
    /// </summary>
    [Fact]
    public void 任务表格固定宽度列必须有不可压缩下限()
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), "Views", "JobsPage.xaml"));
        var grid = document.Descendants()
            .Single(element => element.Name.LocalName == "DataGrid" &&
                               (string?)element.Attribute("ItemsSource") == "{Binding Jobs}");

        var problems = new List<string>();
        foreach (var column in grid.Descendants()
                     .Where(element => element.Name.LocalName.StartsWith("DataGrid") &&
                                       element.Name.LocalName.EndsWith("Column")))
        {
            var width = ParseDataGridLength((string?)column.Attribute("Width"), out var isStar);
            var minWidth = ParseDataGridLength((string?)column.Attribute("MinWidth"), out _);
            if (isStar || minWidth >= width)
            {
                continue;
            }

            problems.Add(
                $"固定列「{(string?)column.Attribute("Header") ?? "(未命名)"}」Width={width} 但 MinWidth={minWidth}");
        }

        Assert.True(problems.Count == 0,
            "任务表存在会被窄窗口压碎的列：" + string.Join("；", problems));
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位虚拟机页面。");
    }
}
