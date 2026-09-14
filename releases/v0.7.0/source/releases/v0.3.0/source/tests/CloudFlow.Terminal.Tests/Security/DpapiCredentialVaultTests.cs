using System.IO;
using CloudFlow.Terminal.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Terminal.Tests.Security;

/// <summary>
/// DPAPI 凭据保险库。密文与元数据分文件、CurrentUser 加密、原子写入 —— 逻辑移植自
/// RemoteFlow 的 DpapiCredentialVault，安全语义必须与之一致。
/// </summary>
public sealed class DpapiCredentialVaultTests
{
    private static (DpapiCredentialVault Vault, string Path) Build()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"), "ssh-vault.dat");
        return (new DpapiCredentialVault(path, NullLogger<DpapiCredentialVault>.Instance), path);
    }

    [Fact]
    public async Task 存取往返_明文一致()
    {
        var (vault, _) = Build();
        const string secret = "p@ssw0rd-测试-🔑";

        var reference = await vault.StoreSecretAsync("ssh:abc", secret);

        Assert.Equal("ssh:abc", reference);
        Assert.Equal(secret, await vault.RetrieveSecretAsync("ssh:abc"));
    }

    [Fact]
    public async Task 删除后取回为空()
    {
        var (vault, _) = Build();
        await vault.StoreSecretAsync("ssh:abc", "secret");

        await vault.DeleteSecretAsync("ssh:abc");

        Assert.Null(await vault.RetrieveSecretAsync("ssh:abc"));
    }

    [Fact]
    public async Task 密文文件里不含明文()
    {
        var (vault, path) = Build();
        await vault.StoreSecretAsync("ssh:abc", "super-secret-明文");

        var raw = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain("super-secret", raw);
    }

    [Fact]
    public async Task 损坏的密文_取回返回null而不是抛出()
    {
        var (vault, path) = Build();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 非法 Base64 —— 模拟 vault 被外部工具改坏
        await File.WriteAllTextAsync(path, """{"ssh:abc": "不是base64!!!"}""");

        Assert.Null(await vault.RetrieveSecretAsync("ssh:abc"));
    }

    [Fact]
    public async Task 损坏的文件结构_存入时显式报错_不静默重建()
    {
        var (vault, path) = Build();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ 这不是 JSON ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vault.StoreSecretAsync("ssh:abc", "x"));
    }

    [Fact]
    public async Task 其他熵加密的密文_本库解不开_返回null()
    {
        // 模拟"从别的应用/别的用途拷来的密文"：熵不同 → 解密必须失败而非给出内容
        var (vault, path) = Build();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var foreign = System.Security.Cryptography.ProtectedData.Protect(
            "other"u8.ToArray(), "OtherApp.Entropy.v1"u8.ToArray(),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        await File.WriteAllTextAsync(path, $$"""{"ssh:abc": "{{Convert.ToBase64String(foreign)}}"}""");

        Assert.Null(await vault.RetrieveSecretAsync("ssh:abc"));
    }
}
