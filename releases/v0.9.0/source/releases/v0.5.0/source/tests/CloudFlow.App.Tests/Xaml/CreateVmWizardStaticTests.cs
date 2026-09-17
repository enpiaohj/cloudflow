using Xunit;

namespace CloudFlow.App.Tests.Xaml;

/// <summary>
/// 创建向导与提交入口的静态护栏。
/// App 测试项目不引用 WPF，所以这里验证两端源码约定，专门防止“编译通过但密码误进 Payload”的回归。
/// </summary>
public sealed class CreateVmWizardStaticTests
{
    [Fact]
    public void 向导必须保留两步网络与认证输入()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "CreateVmWizardDialog.xaml"));

        foreach (var name in new[]
                 {
                     "BasicPanel", "NetworkPanel", "SubscriptionBox", "ResourceGroupBox", "RegionBox",
                     "VirtualNetworkBox", "VnetAddressSpaceBox", "SubnetNameBox", "SubnetAddressPrefixBox",
                     "SshPublicKeyBox", "AdminPasswordBox", "CredentialNameBox",
                     "BackButton", "NextButton", "CreateButton"
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("创建 Standard 静态公网 IP", xaml, StringComparison.Ordinal);
        Assert.Contains("仍不创建 NSG", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 向导参数字典不得包含密码且提交端只传凭据Id()
    {
        var dialog = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "CreateVmWizardDialog.xaml.cs"));
        var parameterStart = dialog.IndexOf("var parameters = new Dictionary", StringComparison.Ordinal);
        var parameterEnd = dialog.IndexOf("Result = new CreateVmResult", StringComparison.Ordinal);

        Assert.True(parameterStart >= 0 && parameterEnd > parameterStart,
            "未找到向导构造 parameters 与返回 Result 的代码块，安全扫描无法建立范围。");
        var parameterBlock = dialog[parameterStart..parameterEnd];
        Assert.DoesNotContain("AdminPasswordBox", parameterBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadCredentialId", parameterBlock, StringComparison.Ordinal);
        Assert.Contains("密码本体永远不进入 parameters", parameterBlock, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "VirtualMachinesViewModel.cs"));
        Assert.Contains("CredentialLibrary.CreateAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("parameters[CreateVmHandler.PayloadCredentialId] = passwordCredential.Id.ToString()", viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[\"password\"]", viewModel, StringComparison.OrdinalIgnoreCase);

        var handler = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Modules", "CloudFlow.Modules.Compute", "Operations", "CreateVmHandler.cs"));
        Assert.Contains("创建虚拟机请求不得包含明文敏感字段", handler, StringComparison.Ordinal);
        Assert.Contains("key.Contains(\"password\"", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void 列表创建入口必须打开向导而不是旧占位提示()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "ViewModels", "VirtualMachinesViewModel.cs"));

        Assert.Contains("new Views.CreateVmWizardDialog", source, StringComparison.Ordinal);
        Assert.Contains("_provisioning.CreateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("创建虚拟机属于预配（Provisioning）能力，不属于 P1", source, StringComparison.Ordinal);
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
            $"从 {AppContext.BaseDirectory} 向上未找到 CloudFlow.sln，无法定位创建向导源码。");
    }
}
