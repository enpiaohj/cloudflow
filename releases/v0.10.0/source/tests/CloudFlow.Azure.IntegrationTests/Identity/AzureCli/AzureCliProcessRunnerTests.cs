using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// AzureCliProcessRunner 行为测试（P0 Spike 步骤 2）。
/// 用临时 .cmd 脚本作为 CLI 替身，验证完整路径强制、子进程级 AZURE_CONFIG_DIR 隔离、
/// 超时终止进程树与输出脱敏；不依赖真实 Runtime 或已登录账户。
/// </summary>
public sealed class AzureCliProcessRunnerTests : IDisposable
{
    private readonly AzureCliProcessRunner _runner = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cfrunner-" + Guid.NewGuid().ToString("N"));

    private string WriteScript(string name, string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Run_拒绝裸命令名与相对路径()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.RunAsync(new AzureCliInvocation { ExecutablePath = "az" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _runner.RunAsync(new AzureCliInvocation { ExecutablePath = "bin/az.cmd" }));
    }

    [Fact]
    public async Task Run_捕获输出与退出码()
    {
        var script = WriteScript("ok.cmd", "@echo hello-cloudflow\r\n@exit /b 0");

        var result = await _runner.RunAsync(new AzureCliInvocation { ExecutablePath = script });

        Assert.True(result.Succeeded);
        Assert.Contains("hello-cloudflow", result.StandardOutput);
    }

    [Fact]
    public async Task Run_配置目录仅注入子进程并自动创建()
    {
        var script = WriteScript("showconfig.cmd", "@echo CONFIG=[%AZURE_CONFIG_DIR%]\r\n@exit /b 0");
        var configDirectory = Path.Combine(_directory, "cli-profile-a");

        var result = await _runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = script,
            ConfigDirectory = configDirectory
        });

        Assert.True(Directory.Exists(configDirectory));
        Assert.Contains(configDirectory, result.StandardOutput);
    }

    [Fact]
    public async Task Run_未提供配置目录时清除环境继承()
    {
        var script = WriteScript("showconfig.cmd", "@echo CONFIG=[%AZURE_CONFIG_DIR%]\r\n@exit /b 0");
        Environment.SetEnvironmentVariable("AZURE_CONFIG_DIR", _directory);
        try
        {
            var result = await _runner.RunAsync(new AzureCliInvocation { ExecutablePath = script });

            // 子进程不得继承环境中的 AZURE_CONFIG_DIR（防误用用户全局 Profile）
            Assert.Contains("CONFIG=[]", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CONFIG_DIR", null);
        }
    }

    [Fact]
    public async Task Run_超时终止进程并抛出脱敏异常()
    {
        var script = WriteScript("slow.cmd", "@ping -n 6 127.0.0.1 > nul\r\n@exit /b 0");
        var startedAt = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            _runner.RunAsync(new AzureCliInvocation
            {
                ExecutablePath = script,
                Timeout = TimeSpan.FromMilliseconds(500)
            }));

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Assert.Contains("超时", exception.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), "超时后应尽快终止进程树");
    }

    [Fact]
    public async Task Run_逐行回调输出内容()
    {
        var script = WriteScript("lines.cmd", "@echo line-one\r\n@echo line-two 1>&2\r\n@exit /b 0");
        var lines = new List<string>();

        var result = await _runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = script,
            OnOutputLine = lines.Add
        });

        Assert.True(result.Succeeded);
        Assert.Contains("line-one", lines);
        Assert.Contains(lines, line => line.TrimEnd().StartsWith("line-two"));
        // 回调之外，完整输出仍可用于解析
        Assert.Contains("line-one", result.StandardOutput);
        Assert.Contains("line-two", result.StandardError);
    }

    [Fact]
    public void Redactor_掩盖敏感JSON值()
    {
        var redacted = AzureCliOutputRedactor.Redact(
            """{"accessToken":"super-secret-value","subscriptionId":"sub-001","clientSecret":"abc","name":"vm-1"}""");

        Assert.DoesNotContain("super-secret-value", redacted);
        Assert.DoesNotContain("abc", redacted);
        Assert.Contains("subscriptionId", redacted);
        Assert.Contains("vm-1", redacted);
        Assert.Contains(AzureCliOutputRedactor.Mask, redacted);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        // 被终止的进程树可能短暂仍持有句柄：短重试后再放弃（不影响断言结果）
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
