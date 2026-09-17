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
    public void Resolve_Runtime缺失时提示通过CloudFlow修复()
    {
        var manager = new AzureCliRuntimeManager(
            new AzureCliProcessRunner(),
            explicitAzCmdPath: Path.Combine(_directory, "missing", "az.cmd"),
            // 注入空探测路径，屏蔽开发机真实 Runtime，保证确定性
            probePaths: []);

        var exception = Assert.Throws<AzureCliException>(() => manager.ResolveAzCmd());

        Assert.Contains("CloudFlow", exception.Message);
        Assert.Contains("Runtime", exception.Message);
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
