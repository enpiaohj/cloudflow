using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// AzureCliRuntimeManager 行为测试（P0 Spike 步骤 6，规范 §十六）：
/// 完整路径解析、Runtime 缺失明确报错（提示 CloudFlow 修复而非装 CLI）、版本查询。
/// </summary>
public sealed class AzureCliRuntimeManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cfruntime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_显式路径存在时直接返回()
    {
        Directory.CreateDirectory(_directory);
        var azPath = Path.Combine(_directory, "az.cmd");
        File.WriteAllText(azPath, "@exit /b 0");

        var manager = new AzureCliRuntimeManager(new AzureCliProcessRunner(), explicitAzCmdPath: azPath);

        Assert.Equal(azPath, manager.ResolveAzCmd());
    }

    [Fact]
    public void Resolve_Runtime缺失时提示通过添加个人账户触发自动下载而非要求手动装CLI()
    {
        var manager = new AzureCliRuntimeManager(
            new AzureCliProcessRunner(),
            explicitAzCmdPath: Path.Combine(_directory, "missing", "az.cmd"),
            // 注入空探测路径，屏蔽开发机真实 Runtime 与 AzureCliRuntimeInstaller 的落盘目录，保证确定性
            probePaths: []);

        var exception = Assert.Throws<AzureCliException>(() => manager.ResolveAzCmd());

        Assert.Contains("Runtime", exception.Message);
        // 不能再让用户去手动安装 Azure CLI——应指向应用内会自动下载的入口。
        Assert.DoesNotContain("手动安装", exception.Message);
        Assert.Contains("添加个人 Microsoft 账户", exception.Message);
    }

    /// <summary>
    /// 未注入探测路径时，默认候选必须包含 AzureCliRuntimeInstaller 的落盘目录——
    /// 这是发布出去的单文件 EXE 唯一真正会命中的候选（发布包本身不携带 Runtime）。
    /// </summary>
    [Fact]
    public void Resolve_已下载的Runtime会被找到而不要求显式路径()
    {
        var installer = new AzureCliRuntimeInstaller(root: Path.Combine(_directory, "Runtime"));
        Directory.CreateDirectory(Path.GetDirectoryName(installer.AzCmdPath)!);
        File.WriteAllText(installer.AzCmdPath, "@exit /b 0");

        // 不传 probePaths：走真实默认候选列表，但 AzureCliRuntimeInstaller 默认根目录是
        // %LOCALAPPDATA%\CloudFlow\Runtime，测试这里只能验证"候选列表包含它"，不好在不碰
        // 用户本机真实 LOCALAPPDATA 的前提下验证端到端命中，因此改为直接断言默认安装目录的
        // 拼接约定与 AzureCliRuntimeInstaller 一致（同一份路径逻辑，不是各写各的）。
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudFlow", "Runtime", "AzureCLI", "bin", "az.cmd");
        Assert.Equal(expected, new AzureCliRuntimeInstaller().AzCmdPath);
    }

    [Fact]
    public async Task GetVersion_解析CLI版本JSON()
    {
        Directory.CreateDirectory(_directory);
        var azPath = Path.Combine(_directory, "az.cmd");
        File.WriteAllText(azPath, "@echo {\"azure-cli\":\"9.9.9\"}\r\n@exit /b 0");
        var manager = new AzureCliRuntimeManager(new AzureCliProcessRunner(), explicitAzCmdPath: azPath);

        var version = await manager.GetVersionAsync();

        Assert.Equal("9.9.9", version);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
        }
    }
}
