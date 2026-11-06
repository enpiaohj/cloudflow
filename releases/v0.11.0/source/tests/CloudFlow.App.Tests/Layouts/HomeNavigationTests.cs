using System.Xml.Linq;
using Xunit;
namespace CloudFlow.App.Tests.Layouts;
public class HomeNavigationTests
{
    [Fact] public void 首页四张统计卡必须有可键盘操作的跳转入口()
    {
        var root = FindRoot();
        var doc = XDocument.Load(Path.Combine(root, "src/CloudFlow.App/Views/HomePage.xaml"));
        var stats = doc.Descendants().Single(e => e.Name.LocalName == "UniformGrid" && (string?)e.Attribute("Columns") == "4");
        Assert.Equal(4, stats.Elements().Count());
        Assert.All(stats.Elements(), card =>
        {
            Assert.Equal("Button", card.Name.LocalName);
            Assert.Contains("ViewVmSummaryCommand", (string?)card.Attribute("Command") ?? "");
        });
    }
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CloudFlow.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
