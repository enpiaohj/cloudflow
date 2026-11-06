using System.Reflection;
using CloudFlow.Azure.Cost;
using Xunit;
namespace CloudFlow.Azure.IntegrationTests.Cost;
public class CostQueryTests
{
    [Theory]
    [InlineData("oops", "USD")]
    [InlineData("1.2", "")]
    public void 非法金额或缺少币种不能伪装为零美元(string cost, string currency)
    {
        var json=System.Text.Json.JsonSerializer.Serialize(new { properties=new { columns=new[]{new{name="Cost"},new{name="Currency"}},rows=new[]{new[]{cost,currency}} } });
        var parse=typeof(ArmCostService).GetMethod("Parse",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Null(parse.Invoke(null,[json]));
    }

    private static object? Call(string name, params object[] args) => typeof(ArmCostService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    [Fact]
    public void 日期包含首尾UTC整天且按资源分组()
    {
        using var doc = System.Text.Json.JsonDocument.Parse((string)Call("BuildQueryBody", new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), true)!);
        Assert.Equal("2024-02-01T00:00:00Z", doc.RootElement.GetProperty("timePeriod").GetProperty("from").GetString());
        Assert.Equal("2024-02-29T23:59:59Z", doc.RootElement.GetProperty("timePeriod").GetProperty("to").GetString());
        Assert.Equal("ResourceId", doc.RootElement.GetProperty("dataset").GetProperty("grouping")[0].GetProperty("name").GetString());
    }
    [Theory]
    [InlineData("https://evil.example/subscriptions/s/providers/Microsoft.CostManagement/query")]
    [InlineData("https://management.azure.com/subscriptions/other/providers/Microsoft.CostManagement/query")]
    [InlineData("http://management.azure.com/subscriptions/s/providers/Microsoft.CostManagement/query")]
    public void 分页地址不得跨域或跨订阅(string next)
    {
        var ex = Assert.Throws<TargetInvocationException>(() => Call("ValidateNextLink", next, "https://management.azure.com/subscriptions/s/providers/Microsoft.CostManagement/query"));
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }
    [Fact]
    public void 金额按列名读取且保留抵扣零费用和未关联资源()
    {
        var json = """{"properties":{"columns":[{"name":"Currency"},{"name":"ResourceId"},{"name":"PreTaxCost"},{"name":"ResourceGroup"}],"rows":[["USD","/resources/vm1","-2.50","rg"],["USD","",0,""]]}}""";
        var page = ((ResourceCost[], string?))Call("ParseCostPage", json, "s", true)!;
        Assert.Equal(-2.5m, page.Item1[0].Amount);
        Assert.Equal("vm1", page.Item1[0].ResourceName);
        Assert.Equal(0m, page.Item1[1].Amount);
        Assert.Contains("未关联资源", page.Item1[1].ResourceName);
    }
    [Fact]
    public void 不同币种不得相加()
    {
        var report = new CostReport([new("s", "", "", 10, "USD"), new("s", "", "", 20, "CNY"), new("s", "", "", -2, "USD")], [], DateTimeOffset.UtcNow);
        Assert.Contains("8.00 USD", report.TotalText); Assert.Contains("20.00 CNY", report.TotalText);
    }

    [Fact]
    public void 资源组汇总隔离订阅币种并保留未关联费用()
    {
        var report = new CostReport([new("s", "/a", "rg", 10, "USD"), new("s", "/a", "RG", -2, "USD"), new("s2", "/b", "rg", 7, "USD"), new("s", "/a", "rg", 2, "CNY"), new("s", "", "", 3, "USD")], [], DateTimeOffset.UtcNow);
        var method = typeof(CostReport).GetMethod("GroupByResourceGroup");
        Assert.NotNull(method);
        var groups = ((System.Collections.IEnumerable)method!.Invoke(report, null)!).Cast<object>().ToArray();
        Assert.Equal(4, groups.Length);
        object? Value(object row, string name) => row.GetType().GetProperty(name)!.GetValue(row);
        var usd = groups.Single(g => (string)Value(g, "SubscriptionId")! == "s" && (string)Value(g, "Currency")! == "USD" && !string.IsNullOrEmpty((string)Value(g, "ResourceGroup")!));
        Assert.Equal(8m, Value(usd, "Amount")); Assert.Equal(1, Value(usd, "ResourceCount"));
        Assert.Contains(groups, g => (string)Value(g, "ResourceGroup")! == "" && (decimal)Value(g, "Amount")! == 3m);
    }
}
