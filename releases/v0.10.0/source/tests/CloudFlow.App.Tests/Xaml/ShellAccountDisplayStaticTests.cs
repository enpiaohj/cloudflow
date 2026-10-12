using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 顶栏账户显示的静态护栏。App 测试项目不引用 WPF，按 <see cref="CreateVmWizardStaticTests"/>
/// 的做法直接核对源码约定。
/// </summary>
public sealed class ShellAccountDisplayStaticTests
{
    [Fact]
    public void 顶栏账户显示必须用登录名而不是可能重复的展示名()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "ShellViewModel.cs"));

        // 真实反馈过：两个账户的展示名完全一样（比如企业与个人账户都叫同一个人名），
        // 顶栏只显示展示名时分不清当前生效的是哪一个；UPN（登录名）在同一 Provider 下
        // 才是真正唯一、能一眼分辨的值。
        var start = viewModel.IndexOf("public string AccountDisplayName", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 AccountDisplayName，无法核对顶栏账户显示逻辑。");
        var end = viewModel.IndexOf('\n', start);
        var line = viewModel[start..end];

        Assert.Contains("Account.Username", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAccount?.DisplayName", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAccount.DisplayName", line, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位顶栏账户显示源码。");
    }
}
