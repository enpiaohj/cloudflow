using System.Diagnostics;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Runtime;

/// <summary>
/// P0 Spike 步骤 1：嵌入式 Azure CLI Runtime 静态验证。
/// 认证版本 azure-cli 2.90.0（Microsoft 官方 x64 ZIP），SHA-256 记录于 docs/Identity-Spike.md。
/// Runtime 未下载/解包时本测试静默返回（与 SilentAuthTests 同一约定）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureCliRuntimeSmokeTests
{
    public const string CertifiedVersion = "2.90.0";

    public const string CertifiedZipSha256 =
        "c4ef59b14f0edd074427fd9981e57b0780965ccdcf6191c033fdf4b4361f33d7";

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CloudFlow.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private static string? FindAzCmd()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }
        var path = Path.Combine(root, "runtime", "AzureCLI", "bin", "az.cmd");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task AzCmd_在隔离配置目录下报告认证版本()
    {
        var azCmd = FindAzCmd();
        if (azCmd is null)
        {
            // Runtime 未就绪：跳过静态验证
            return;
        }

        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(azCmd))!;
        var isolatedConfigDir = Path.Combine(runtimeRoot, "smoke-config");

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{azCmd}\" version\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // 仅对该子进程生效的配置隔离（P0 Spike 步骤：AZURE_CONFIG_DIR）
        startInfo.EnvironmentVariables["AZURE_CONFIG_DIR"] = isolatedConfigDir;

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains(CertifiedVersion, output);
    }
}
