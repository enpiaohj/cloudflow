using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 成本属于账单信息；Demo 模式不能用示例金额填充首页，避免被误读为真实支出。
/// </summary>
public sealed class CostInsightStaticTests
{
    [Fact]
    public void 未登录时成本服务必须返回无数据而非演示金额()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "Infrastructure", "HybridCostService.cs"));

        Assert.Contains("请登录 Azure 后查看真实成本数据", source, StringComparison.Ordinal);
        Assert.Contains("((CostSummary?)null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("482.21m", source, StringComparison.Ordinal);
        Assert.DoesNotContain("演示读数", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 首页默认成本状态必须是中性无数据()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("private string _costAmount = \"—\"", source, StringComparison.Ordinal);
        Assert.Contains("private bool _hasCostData;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$482.21", source, StringComparison.Ordinal);
        Assert.DoesNotContain("↓ 12%", source, StringComparison.Ordinal);
    }

    private static string AppDirectory() => Path.Combine(RepositoryRoot(), "src", "CloudFlow.App");

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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位成本洞察源码。");
    }
}
