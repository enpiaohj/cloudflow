using System.Xml.Linq;
using Xunit;
namespace CloudFlow.App.Tests.Layouts;
public class JobDetailsEntryTests
{
    [Theory]
    [InlineData("HomePage.xaml", "{Binding RecentOperations}")]
    [InlineData("JobsPage.xaml", "{Binding Jobs}")]
    public void 操作记录列表必须提供双击和显式详情入口(string page, string source)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null && !File.Exists(Path.Combine(dir.FullName, "CloudFlow.sln"))) dir=dir.Parent;
        var doc=XDocument.Load(Path.Combine(dir!.FullName,"src/CloudFlow.App/Views",page));
        var grid=doc.Descendants().Single(e=>e.Name.LocalName=="DataGrid" && (string?)e.Attribute("ItemsSource")==source);
        Assert.Contains(grid.Attributes(),a=>a.Name.LocalName=="JobDetailsBehavior.Enabled" && a.Value=="True");
        Assert.Contains(grid.Descendants(),e=>e.Name.LocalName=="Button" && (string?)e.Attribute("Content")=="详情");
    }
}
