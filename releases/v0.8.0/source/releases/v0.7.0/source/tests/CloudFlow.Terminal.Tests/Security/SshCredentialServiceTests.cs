using System.IO;
using System.Text.Json;
using CloudFlow.Terminal.Security;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CloudFlow.Terminal.Tests.Security;

/// <summary>
/// 凭据服务：三条不变式（重名拒绝 / 提交后清理旧密文 / 删除先提交元数据）、
/// "null = 保持 / 空串 = 清除"的更新语义、以及认证方式由 AuthType 决定。
/// </summary>
public sealed class SshCredentialServiceTests
{
    /// <summary>一个能被断言"日志里没出现过明文"的日志器。</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private sealed class Fixture
    {
        public required string LibraryPath { get; init; }
        public required string VaultPath { get; init; }
        public required RecordingLogger Logger { get; init; }
        public required DpapiCredentialVault Vault { get; init; }
        public required SshCredentialLibrary Library { get; init; }
        public required SshCredentialService Service { get; init; }

        public static Fixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"));
            var libraryPath = Path.Combine(directory, "ssh-credentials-library.json");
            var vaultPath = Path.Combine(directory, "ssh-vault.dat");
            var logger = new RecordingLogger();
            var vault = new DpapiCredentialVault(vaultPath, logger);
            var library = new SshCredentialLibrary(libraryPath, logger);

            return new Fixture
            {
                LibraryPath = libraryPath,
                VaultPath = vaultPath,
                Logger = logger,
                Vault = vault,
                Library = library,
                Service = new SshCredentialService(library, vault, legacyCredentialsPath: null, logger),
            };
        }

        /// <summary>保险库里现存的全部引用键 —— 用来断言"旧密文真的被清掉了"与"没有孤儿"。</summary>
        public async Task<string[]> VaultKeysAsync()
        {
            if (!File.Exists(VaultPath))
            {
                return [];
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(VaultPath));
            return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        }
    }

    private static SshCredential PasswordCredential(string name = "appscloud") =>
        new() { Name = name, Username = "rootadmin", AuthType = SshAuthType.Password };

    private static SshCredential KeyCredential(string name = "appscloud") =>
        new() { Name = name, Username = "rootadmin", AuthType = SshAuthType.PrivateKey };

    // ── 新建与解析 ────────────────────────────────────────────────

    [Fact]
    public async Task 新建密码凭据_可解出原密码()
    {
        var f = Fixture.Create();

        var created = await f.Service.CreateAsync(PasswordCredential(), "CANARY-P@ssw0rd", null);

        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.NotNull(resolved);
        Assert.Equal(SshAuthType.Password, resolved.AuthType);
        Assert.Equal("rootadmin", resolved.Username);
        Assert.Equal("CANARY-P@ssw0rd", resolved.Password);
        Assert.Null(resolved.PrivateKey);
    }

    [Fact]
    public async Task 新建私钥凭据_正文与passphrase都能解出()
    {
        var f = Fixture.Create();

        var created = await f.Service.CreateAsync(KeyCredential(), "the-passphrase", "-----BEGIN KEY-----");

        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.NotNull(resolved);
        Assert.Equal(SshAuthType.PrivateKey, resolved.AuthType);
        Assert.Equal("-----BEGIN KEY-----", resolved.PrivateKey);
        Assert.Equal("the-passphrase", resolved.Passphrase);
        Assert.Null(resolved.Password);
    }

    [Fact]
    public async Task 私钥凭据不带passphrase也能解出_Passphrase为空()
    {
        var f = Fixture.Create();

        var created = await f.Service.CreateAsync(KeyCredential(), null, "-----BEGIN KEY-----");

        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.Equal("-----BEGIN KEY-----", resolved!.PrivateKey);
        Assert.Null(resolved.Passphrase);
    }

    [Fact]
    public async Task 认证方式由AuthType决定_而不是靠哪个密文解出来了()
    {
        var f = Fixture.Create();
        // 私钥凭据的密码槽位里放的是 passphrase。若实现改成"哪个密文解出来了就用哪种方式"，
        // 这条会解成密码认证 —— 用户拿着 passphrase 当密码去连，失败提示还指向错误的排查方向。
        var created = await f.Service.CreateAsync(KeyCredential(), "phrase", "KEY-BODY");

        using var resolved = await f.Service.ResolveAsync(created.Id);

        Assert.Equal(SshAuthType.PrivateKey, resolved!.AuthType);
        Assert.Equal("KEY-BODY", resolved.PrivateKey);
        Assert.Equal("phrase", resolved.Passphrase);
        Assert.Null(resolved.Password);
    }

    [Fact]
    public async Task 解析不存在的凭据返回null_不抛()
    {
        var f = Fixture.Create();

        Assert.Null(await f.Service.ResolveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task 陈旧引用键_解析返回null且不抛()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);

        // 模拟保险库被换成了别的 Windows 账户写的文件：引用键还在，但密文解不开
        await File.WriteAllTextAsync(f.VaultPath, "{}");

        Assert.Null(await f.Service.ResolveAsync(created.Id));
    }

    // ── 校验 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 新建时名称为空_抛出()
    {
        var f = Fixture.Create();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.CreateAsync(PasswordCredential("   "), "pw", null));

        Assert.Contains("名称不能为空", ex.Message);
    }

    [Fact]
    public async Task 新建时用户名为空_抛出()
    {
        var f = Fixture.Create();
        var credential = PasswordCredential();
        credential.Username = " ";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.CreateAsync(credential, "pw", null));

        Assert.Contains("用户名不能为空", ex.Message);
    }

    [Fact]
    public async Task 密码方式不提供密码_抛出且保险库里不留东西()
    {
        var f = Fixture.Create();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.CreateAsync(PasswordCredential(), null, null));

        Assert.Contains("必须提供密码", ex.Message);
        Assert.Empty(await f.VaultKeysAsync());
    }

    [Fact]
    public async Task 私钥方式不提供正文_抛出且保险库里不留东西()
    {
        var f = Fixture.Create();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.CreateAsync(KeyCredential(), "phrase", null));

        Assert.Contains("必须提供私钥内容", ex.Message);
        Assert.Empty(await f.VaultKeysAsync());
    }

    [Fact]
    public async Task 重名在服务层同样被拒_且失败时不留下孤儿密文()
    {
        var f = Fixture.Create();
        await f.Service.CreateAsync(PasswordCredential("appscloud"), "pw-1", null);
        var keysAfterFirst = await f.VaultKeysAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.CreateAsync(PasswordCredential("AppsCloud"), "pw-2", null));

        Assert.Contains("已存在同名凭据", ex.Message);
        // 第二条的密码已经写进保险库了，提交失败必须把它回滚掉
        Assert.Equal(keysAfterFirst, await f.VaultKeysAsync());
    }

    [Fact]
    public async Task 名称占用检查可排除自身()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential("appscloud"), "pw", null);

        Assert.True(await f.Service.IsNameTakenAsync("appscloud"));
        Assert.False(await f.Service.IsNameTakenAsync("appscloud", excludeId: created.Id));
        Assert.False(await f.Service.IsNameTakenAsync("other"));
    }

    // ── 更新语义 ──────────────────────────────────────────────────

    [Fact]
    public async Task 更新时secret为null表示保持原密文不变()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "original", null);
        var referenceBefore = created.SecretReference;

        created.Username = "changed";
        var updated = await f.Service.UpdateAsync(created, secret: null, privateKeyBody: null);

        Assert.Equal(referenceBefore, updated.SecretReference);
        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.Equal("original", resolved!.Password);
    }

    [Fact]
    public async Task 更新时passphrase传空串表示清除()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(KeyCredential(), "phrase", "KEY-BODY");
        var passphraseReference = created.SecretReference;
        Assert.NotNull(passphraseReference);

        var updated = await f.Service.UpdateAsync(created, secret: "", privateKeyBody: null);

        Assert.Null(updated.SecretReference);
        Assert.DoesNotContain(passphraseReference, await f.VaultKeysAsync());
        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.Equal("KEY-BODY", resolved!.PrivateKey);
        Assert.Null(resolved.Passphrase);
    }

    [Fact]
    public async Task 替换密码后新值可解出_旧密文已从保险库消失()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "old-secret", null);
        var oldReference = created.SecretReference!;

        var updated = await f.Service.UpdateAsync(created, secret: "new-secret", privateKeyBody: null);

        Assert.NotEqual(oldReference, updated.SecretReference);
        Assert.DoesNotContain(oldReference, await f.VaultKeysAsync());   // 没有孤儿
        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.Equal("new-secret", resolved!.Password);
    }

    [Fact]
    public async Task 切换认证方式会清掉对侧的引用()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "old-password", null);
        var passwordReference = created.SecretReference!;

        var updated = await f.Service.UpdateAsync(
            new SshCredential
            {
                Id = created.Id,
                Name = created.Name,
                Username = created.Username,
                AuthType = SshAuthType.PrivateKey,
            },
            secret: null,
            privateKeyBody: "KEY-BODY");

        Assert.Null(updated.SecretReference);          // 密码槽位作废
        Assert.NotNull(updated.KeyReference);
        Assert.DoesNotContain(passwordReference, await f.VaultKeysAsync());
        using var resolved = await f.Service.ResolveAsync(created.Id);
        Assert.Equal(SshAuthType.PrivateKey, resolved!.AuthType);
        Assert.Equal("KEY-BODY", resolved.PrivateKey);
    }

    [Fact]
    public async Task 切换回密码方式时必须重新提供密码()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(KeyCredential(), null, "KEY-BODY");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => f.Service.UpdateAsync(
            new SshCredential
            {
                Id = created.Id,
                Name = created.Name,
                Username = created.Username,
                AuthType = SshAuthType.Password,
            },
            secret: null,
            privateKeyBody: null));

        Assert.Contains("必须提供密码", ex.Message);
    }

    [Fact]
    public async Task 更新不存在的凭据_抛出()
    {
        var f = Fixture.Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.UpdateAsync(PasswordCredential(), "pw", null));

        Assert.Contains("凭据不存在", ex.Message);
    }

    [Fact]
    public async Task 更新不修改创建时间()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);
        var createdAt = created.CreatedAt;

        var updated = await f.Service.UpdateAsync(created, secret: "pw2", privateKeyBody: null);

        Assert.Equal(createdAt, updated.CreatedAt);
    }

    [Fact]
    public async Task 元数据写入失败时回滚新密文_保险库里不留孤儿()
    {
        var f = Fixture.Create();
        // 把库文件路径占成一个目录：读时 File.Exists 为 false，写时 File.Move 目标已被占
        Directory.CreateDirectory(f.LibraryPath);

        await Assert.ThrowsAnyAsync<Exception>(
            () => f.Service.CreateAsync(PasswordCredential(), "CANARY-P@ssw0rd", null));

        Assert.Empty(await f.VaultKeysAsync());
    }

    // ── 删除 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 删除凭据会清掉元数据与两条密文以及最近使用()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(KeyCredential(), "phrase", "KEY-BODY");
        await f.Service.MarkUsedAsync(created.Id);

        await f.Service.DeleteAsync(created.Id);

        Assert.Null(await f.Service.GetAsync(created.Id));
        Assert.Empty(await f.VaultKeysAsync());
        Assert.Null(await f.Service.GetLastUsedIdAsync());
    }

    [Fact]
    public async Task 重复删除同一条凭据是幂等的()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);

        await f.Service.DeleteAsync(created.Id);
        await f.Service.DeleteAsync(created.Id);   // 不抛

        Assert.Empty(await f.VaultKeysAsync());
    }

    // ── 最近使用 ──────────────────────────────────────────────────

    [Fact]
    public async Task 最近使用往返_后写覆盖先写()
    {
        var f = Fixture.Create();
        var first = await f.Service.CreateAsync(PasswordCredential("a"), "pw", null);
        var second = await f.Service.CreateAsync(PasswordCredential("b"), "pw", null);

        await f.Service.MarkUsedAsync(first.Id);
        Assert.Equal(first.Id, await f.Service.GetLastUsedIdAsync());

        await f.Service.MarkUsedAsync(second.Id);
        Assert.Equal(second.Id, await f.Service.GetLastUsedIdAsync());
    }

    [Fact]
    public async Task 标记一个不存在的凭据不会污染最近使用()
    {
        var f = Fixture.Create();

        await f.Service.MarkUsedAsync(Guid.NewGuid());

        Assert.Null(await f.Service.GetLastUsedIdAsync());
    }

    [Fact]
    public async Task 最近使用指向已被删除的凭据时读作null()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);
        await f.Service.MarkUsedAsync(created.Id);

        // 绕过服务层直接删元数据，模拟"另一个实例删掉了它"
        await f.Library.DeleteAsync(created.Id);

        Assert.Null(await f.Service.GetLastUsedIdAsync());
    }

    // ── 按 VM 记住凭据 ────────────────────────────────────────────

    private const string VmId =
        "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud";

    private static string OtherVmId => VmId.Replace("appscloud", "hgslt");

    [Fact]
    public async Task 按VM记住往返()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);

        await f.Service.SetDefaultCredentialIdForVmAsync(VmId, created.Id);

        Assert.Equal(created.Id, await f.Service.GetDefaultCredentialIdForVmAsync(VmId));
    }

    [Fact]
    public async Task 记住一个不存在的凭据会被拒绝()
    {
        // 记住一个不存在的主键，下次打开连接框就会拿着死主键去预选，
        // 而"预选停在一个无关选项上"在界面上很难看出原因 —— 所以服务层直接挡掉。
        var f = Fixture.Create();

        await f.Service.SetDefaultCredentialIdForVmAsync(VmId, Guid.NewGuid());

        Assert.Null(await f.Service.GetDefaultCredentialIdForVmAsync(VmId));
    }

    [Fact]
    public async Task 按VM记住指向已被删除的凭据时读作null()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);
        await f.Service.SetDefaultCredentialIdForVmAsync(VmId, created.Id);

        // 绕过服务层直接删元数据，模拟"另一个实例删掉了它"
        await f.Library.DeleteAsync(created.Id);

        Assert.Null(await f.Service.GetDefaultCredentialIdForVmAsync(VmId));
    }

    [Fact]
    public async Task 删除凭据一并清掉记住它的VM_且不残留密文()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);
        await f.Service.SetDefaultCredentialIdForVmAsync(VmId, created.Id);

        await f.Service.DeleteAsync(created.Id);

        Assert.Null(await f.Service.GetDefaultCredentialIdForVmAsync(VmId));
        Assert.Empty(await f.VaultKeysAsync());   // 密文也清干净，保险库里不留孤儿
    }

    [Fact]
    public async Task 引用计数与记住它的VM数一致()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(PasswordCredential(), "pw", null);

        await f.Service.SetDefaultCredentialIdForVmAsync(VmId, created.Id);
        await f.Service.SetDefaultCredentialIdForVmAsync(OtherVmId, created.Id);

        Assert.Equal(2, await f.Service.CountVmsUsingCredentialAsync(created.Id));
    }

    // ── Secret 卫生 ───────────────────────────────────────────────

    private const string Canary = "CANARY-P@ssw0rd-9f3a";

    [Fact]
    public async Task 明文不落盘_文件里既无明文字段也无明文值()
    {
        var f = Fixture.Create();
        await f.Service.CreateAsync(KeyCredential(), Canary, "CANARY-KEY-BODY");

        var libraryJson = await File.ReadAllTextAsync(f.LibraryPath);
        var vaultJson = await File.ReadAllTextAsync(f.VaultPath);

        Assert.DoesNotContain(Canary, libraryJson);
        Assert.DoesNotContain("CANARY-KEY-BODY", libraryJson);
        Assert.DoesNotContain(Canary, vaultJson);          // 保险库里只有 DPAPI 密文
        Assert.DoesNotContain("CANARY-KEY-BODY", vaultJson);

        using var document = JsonDocument.Parse(libraryJson);
        var fieldNames = document.RootElement.GetProperty("Credentials")[0]
            .EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Password", fieldNames);
        Assert.DoesNotContain("Passphrase", fieldNames);
        Assert.DoesNotContain("PrivateKey", fieldNames);
    }

    [Fact]
    public async Task 明文不进日志()
    {
        var f = Fixture.Create();
        var created = await f.Service.CreateAsync(KeyCredential(), Canary, "CANARY-KEY-BODY");

        // 跑遍全路径：更新、解析、解析失败、删除
        await f.Service.UpdateAsync(created, secret: Canary, privateKeyBody: "CANARY-KEY-BODY");
        using (await f.Service.ResolveAsync(created.Id)) { }
        await File.WriteAllTextAsync(f.VaultPath, "{}");
        Assert.Null(await f.Service.ResolveAsync(created.Id));
        await f.Service.DeleteAsync(created.Id);

        Assert.NotEmpty(f.Logger.Messages);
        Assert.DoesNotContain(f.Logger.Messages, m => m.Contains(Canary, StringComparison.Ordinal));
        Assert.DoesNotContain(f.Logger.Messages, m => m.Contains("CANARY-KEY-BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 明文不进异常消息()
    {
        var f = Fixture.Create();
        await f.Service.CreateAsync(PasswordCredential("appscloud"), Canary, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.CreateAsync(PasswordCredential("appscloud"), Canary, null));

        Assert.DoesNotContain(Canary, ex.Message);
        Assert.DoesNotContain(Canary, ex.ToString());
    }

    [Fact]
    public void ToString不泄漏任何秘密面()
    {
        var resolved = new ResolvedSshCredential(
            SshAuthType.PrivateKey, "rootadmin", null, "KEY-BODY", "phrase");
        var credential = new SshCredential
        {
            Name = "appscloud",
            Username = "rootadmin",
            AuthType = SshAuthType.PrivateKey,
            SecretReference = "sshpass:abc",
            KeyReference = "sshkey:def",
        };

        Assert.Equal("ResolvedSshCredential(PrivateKey, rootadmin)", resolved.ToString());
        Assert.Equal("SshCredential(appscloud, PrivateKey, rootadmin)", credential.ToString());
        Assert.DoesNotContain("KEY-BODY", resolved.ToString());
        Assert.DoesNotContain("phrase", resolved.ToString());
        Assert.DoesNotContain("sshpass:abc", credential.ToString());
    }

    [Fact]
    public void Dispose后明文引用被置空()
    {
        var resolved = new ResolvedSshCredential(
            SshAuthType.PrivateKey, "rootadmin", null, "KEY-BODY", "phrase");

        resolved.Dispose();

        Assert.Null(resolved.PrivateKey);
        Assert.Null(resolved.Passphrase);
        Assert.Null(resolved.Password);
    }
}
