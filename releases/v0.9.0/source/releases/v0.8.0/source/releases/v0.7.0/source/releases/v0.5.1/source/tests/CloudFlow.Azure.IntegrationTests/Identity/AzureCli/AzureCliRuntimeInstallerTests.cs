using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// AzureCliRuntimeInstaller 的哈希校验行为——真正的下载/解压对着官方固定发布物做真实端到端
/// 验证不适合放进快速测试（内容的 SHA-256 已钉死在实现里，没法用合成内容满足它，也不该每次
/// 跑测试都去下载 ~90MB）。这里只测独立抽出来的 <see cref="AzureCliRuntimeInstaller.VerifyFileHashAsync"/>
/// ——真正的安全底线（校验失败绝不解压/执行）就压在这一个方法上。
/// </summary>
public sealed class AzureCliRuntimeInstallerTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), "cfruntime-hash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifyFileHashAsync_哈希一致时返回true()
    {
        var content = "CloudFlow test content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFile, content);
        var expected = Convert.ToHexString(SHA256.HashData(content));

        var result = await AzureCliRuntimeInstaller.VerifyFileHashAsync(_tempFile, expected, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task VerifyFileHashAsync_大小写不敏感()
    {
        var content = "CloudFlow test content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFile, content);
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var result = await AzureCliRuntimeInstaller.VerifyFileHashAsync(_tempFile, expected, CancellationToken.None);

        Assert.True(result);
    }

    /// <summary>
    /// 安全底线：内容被篡改（哪怕只改一个字节）必须校验失败——这是"下载内容必须先校验 SHA-256
    /// 完全一致才会被解压/使用"这条纪律的核心断言。
    /// </summary>
    [Fact]
    public async Task VerifyFileHashAsync_内容被篡改时返回false()
    {
        await File.WriteAllBytesAsync(_tempFile, "CloudFlow test content"u8.ToArray());
        var hashOfDifferentContent = Convert.ToHexString(SHA256.HashData("tampered"u8.ToArray()));

        var result = await AzureCliRuntimeInstaller.VerifyFileHashAsync(
            _tempFile, hashOfDifferentContent, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public void InstallDirectory与AzCmdPath拼接约定与ResolveAzCmd的候选路径一致()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfruntime-root-" + Guid.NewGuid().ToString("N"));
        var installer = new AzureCliRuntimeInstaller(root: root);

        Assert.Equal(Path.Combine(root, "AzureCLI"), installer.InstallDirectory);
        Assert.Equal(Path.Combine(root, "AzureCLI", "bin", "az.cmd"), installer.AzCmdPath);
    }

    [Fact]
    public void IsInstalled_未安装时返回false()
    {
        var installer = new AzureCliRuntimeInstaller(
            root: Path.Combine(Path.GetTempPath(), "cfruntime-notinstalled-" + Guid.NewGuid().ToString("N")));

        Assert.False(installer.IsInstalled);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}

/// <summary>
/// 真实端到端验证：实际下载官方发布物、校验、解压，并用解压出来的 az.cmd 真跑一次
/// <c>az version</c> 确认能用——这是"应用将自动分发完整 Runtime"这句承诺第一次真正有测试
/// 覆盖（此前 <c>AzureCliRuntimeSmokeTests</c> 只验证"Runtime 已经在本机时能用"，从没验证过
/// "Runtime 不存在时 CloudFlow 真的能把它装出来"）。需要真实网络，下载 ~90MB，
/// 不适合每次快速测试都跑，归入 Integration 分类。
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureCliRuntimeInstallerRealDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfruntime-e2e-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureInstalledAsync_真实下载校验解压后az_version可用()
    {
        var installer = new AzureCliRuntimeInstaller(root: _root);
        Assert.False(installer.IsInstalled);

        var notes = new List<string>();
        await installer.EnsureInstalledAsync(notes.Add, CancellationToken.None);

        Assert.True(installer.IsInstalled);
        Assert.True(File.Exists(installer.AzCmdPath));
        Assert.NotEmpty(notes);

        var runner = new AzureCliProcessRunner();
        var result = await runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = installer.AzCmdPath,
            Arguments = ["version", "--output", "json"],
            ConfigDirectory = Path.Combine(_root, "smoke-config")
        });

        Assert.True(result.Succeeded, result.StandardError);
        using var doc = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            AzureCliRuntimeInstaller.CertifiedVersion,
            doc.RootElement.GetProperty("azure-cli").GetString());

        // 第二次调用必须直接短路，不重新下载——已装好的 Runtime 不该被重复处理。
        await installer.EnsureInstalledAsync(_ => throw new InvalidOperationException(
            "已安装时不应再上报下载进度"), CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
