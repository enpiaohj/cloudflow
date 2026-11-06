using System.IO;
using System.Text.Json;
using CloudFlow.Terminal.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Terminal.Tests.Security;

/// <summary>
/// 凭据库（ssh-credentials-library.json）的文档读写与旧格式迁移。
/// 关注点：文件里只有引用键、schema 版本纪律、迁移只搬运引用键不做加解密。
/// </summary>
public sealed class SshCredentialLibraryTests
{
    private static SshCredentialLibrary Build(out string libraryPath, out string legacyPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"));
        libraryPath = Path.Combine(directory, "ssh-credentials-library.json");
        legacyPath = Path.Combine(directory, "ssh-credentials.json");
        return new SshCredentialLibrary(libraryPath);
    }

    private static SshCredential Credential(string name, SshAuthType type = SshAuthType.Password) => new()
    {
        Name = name,
        Username = "rootadmin",
        AuthType = type,
        SecretReference = type == SshAuthType.Password ? "ssh:ref-1" : "sshpass:ref-1",
        KeyReference = type == SshAuthType.PrivateKey ? "sshkey:ref-2" : null,
    };

    private static SshCredentialMeta LegacyMeta(string resourceId, string username = "azureuser") => new()
    {
        ResourceId = resourceId,
        Username = username,
        AuthType = SshAuthType.PrivateKey,
        SecretReference = "sshpass:legacy-" + Guid.NewGuid().ToString("N"),
        KeyReference = "sshkey:legacy-" + Guid.NewGuid().ToString("N"),
        PrivateKeyPath = @"C:\keys\deploy.pem",
        SavedAt = new DateTimeOffset(2026, 8, 20, 13, 45, 0, TimeSpan.FromHours(8)),
    };

    // ── 基本读写 ──────────────────────────────────────────────────

    [Fact]
    public async Task 库文件不存在时列出为空_不抛()
    {
        var library = Build(out _, out _);

        Assert.Empty(await library.ListAsync());
    }

    [Fact]
    public async Task 保存后能按Id与名称查到()
    {
        var library = Build(out _, out _);
        var credential = Credential("appscloud");

        await library.SaveAsync(credential);

        Assert.Equal("appscloud", (await library.GetAsync(credential.Id))!.Name);
        Assert.Single(await library.ListAsync());
    }

    [Fact]
    public async Task 列表按名称排序_忽略大小写()
    {
        var library = Build(out _, out _);
        await library.SaveAsync(Credential("zeta"));
        await library.SaveAsync(Credential("Alpha"));
        await library.SaveAsync(Credential("beta"));

        var names = (await library.ListAsync()).Select(c => c.Name).ToArray();

        Assert.Equal(["Alpha", "beta", "zeta"], names);
    }

    [Fact]
    public async Task 重名被拒绝_仅大小写不同也算重名()
    {
        var library = Build(out _, out _);
        await library.SaveAsync(Credential("appscloud"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => library.SaveAsync(Credential("AppsCloud")));

        Assert.Contains("已存在同名凭据", ex.Message);
    }

    [Fact]
    public async Task 编辑自身不算重名()
    {
        var library = Build(out _, out _);
        var credential = Credential("appscloud");
        await library.SaveAsync(credential);

        credential.Username = "changed";
        await library.SaveAsync(credential);

        Assert.Equal("changed", (await library.ListAsync()).Single().Username);
    }

    [Fact]
    public async Task 覆盖保存返回被覆盖的旧记录()
    {
        var library = Build(out _, out _);
        var credential = Credential("appscloud");
        await library.SaveAsync(credential);

        credential.SecretReference = "ssh:ref-new";
        var previous = await library.SaveAsync(credential);

        Assert.Equal("ssh:ref-1", previous!.SecretReference);
    }

    [Fact]
    public async Task 删除不存在的Id返回null_不抛()
    {
        var library = Build(out _, out _);

        Assert.Null(await library.DeleteAsync(Guid.NewGuid()));
    }

    // ── "最近使用" ────────────────────────────────────────────────

    [Fact]
    public async Task 最近使用往返_可清空()
    {
        var library = Build(out _, out _);
        var credential = Credential("appscloud");
        await library.SaveAsync(credential);

        await library.SetLastUsedIdAsync(credential.Id);
        Assert.Equal(credential.Id, await library.GetLastUsedIdAsync());

        await library.SetLastUsedIdAsync(null);
        Assert.Null(await library.GetLastUsedIdAsync());
    }

    [Fact]
    public async Task 删除凭据会一并清掉指向它的最近使用()
    {
        var library = Build(out _, out _);
        var credential = Credential("appscloud");
        await library.SaveAsync(credential);
        await library.SetLastUsedIdAsync(credential.Id);

        await library.DeleteAsync(credential.Id);

        // 否则下次打开连接框会预选一个已经不存在的主键
        Assert.Null(await library.GetLastUsedIdAsync());
    }

    [Fact]
    public async Task 删除其他凭据不会动最近使用()
    {
        var library = Build(out _, out _);
        var keeper = Credential("keeper");
        var other = Credential("other");
        await library.SaveAsync(keeper);
        await library.SaveAsync(other);
        await library.SetLastUsedIdAsync(keeper.Id);

        await library.DeleteAsync(other.Id);

        Assert.Equal(keeper.Id, await library.GetLastUsedIdAsync());
    }

    // ── 文件纪律 ──────────────────────────────────────────────────

    [Fact]
    public async Task 删光全部凭据后库文件仍然存在且为空数组()
    {
        var library = Build(out var libraryPath, out _);
        var credential = Credential("appscloud");
        await library.SaveAsync(credential);

        await library.DeleteAsync(credential.Id);

        // 文件一旦消失，迁移会把旧记录重新迁一遍 —— 用户删掉的凭据就复活了
        Assert.True(File.Exists(libraryPath));
        Assert.Empty(await library.ListAsync());
    }

    [Fact]
    public async Task schema版本高于本版本时显式失败_且不改写文件()
    {
        var library = Build(out var libraryPath, out _);
        await File.WriteAllTextAsync(
            libraryPath,
            """{"SchemaVersion":3,"LastUsedCredentialId":null,"Credentials":[]}""");
        var before = await File.ReadAllBytesAsync(libraryPath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => library.ListAsync());

        Assert.Contains("更高版本", ex.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(libraryPath));
    }

    [Fact]
    public async Task 文件损坏时显式失败_不静默重建()
    {
        var library = Build(out var libraryPath, out _);
        await File.WriteAllTextAsync(libraryPath, "{ this is not json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => library.ListAsync());

        Assert.Contains("已损坏", ex.Message);
        // 不重建 = 原文件保持原样，用户还有机会自己救
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(libraryPath));
    }

    [Fact]
    public async Task 密码方式却残留私钥引用时_读时归一化丢弃该引用()
    {
        var library = Build(out var libraryPath, out _);
        await File.WriteAllTextAsync(libraryPath, """
            {"SchemaVersion":1,"LastUsedCredentialId":null,"Credentials":[
              {"Id":"11111111-1111-1111-1111-111111111111","Name":"dirty","Username":"u",
               "AuthType":"Password","SecretReference":"ssh:keep","KeyReference":"sshkey:stale",
               "PrivateKeyPath":"C:\\keys\\x.pem","Description":"",
               "CreatedAt":"2026-01-01T00:00:00+08:00","UpdatedAt":"2026-01-01T00:00:00+08:00"}]}
            """);

        var credential = (await library.ListAsync()).Single();

        Assert.Equal("ssh:keep", credential.SecretReference);
        Assert.Null(credential.KeyReference);
        Assert.Null(credential.PrivateKeyPath);
    }

    [Fact]
    public async Task 私钥方式带着SecretReference是合法的_那是passphrase()
    {
        var library = Build(out var libraryPath, out _);
        await File.WriteAllTextAsync(libraryPath, """
            {"SchemaVersion":1,"LastUsedCredentialId":null,"Credentials":[
              {"Id":"11111111-1111-1111-1111-111111111111","Name":"keyed","Username":"u",
               "AuthType":"PrivateKey","SecretReference":"sshpass:phrase","KeyReference":"sshkey:body",
               "Description":"","CreatedAt":"2026-01-01T00:00:00+08:00","UpdatedAt":"2026-01-01T00:00:00+08:00"}]}
            """);

        var credential = (await library.ListAsync()).Single();

        Assert.Equal("sshpass:phrase", credential.SecretReference);
        Assert.Equal("sshkey:body", credential.KeyReference);
    }

    [Fact]
    public async Task 文件里只有引用键_没有任何可以放明文的字段()
    {
        var library = Build(out var libraryPath, out _);
        await library.SaveAsync(Credential("appscloud"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath));
        var fieldNames = document.RootElement.GetProperty("Credentials")[0]
            .EnumerateObject().Select(p => p.Name).ToArray();

        // 明文一旦有字段可落，迟早会有人往里写。引用键是唯一的秘密载体。
        Assert.DoesNotContain("Password", fieldNames);
        Assert.DoesNotContain("Passphrase", fieldNames);
        Assert.DoesNotContain("PrivateKey", fieldNames);
        Assert.Contains("SecretReference", fieldNames);
        Assert.Contains("KeyReference", fieldNames);
    }

    // ── 迁移 ──────────────────────────────────────────────────────

    private static async Task WriteLegacyAsync(string legacyPath, params SshCredentialMeta[] metas) =>
        await AtomicJson.SaveAsync(legacyPath, metas.ToList());

    [Fact]
    public async Task 迁移运引用键_逐字节复用不重新加解密()
    {
        var library = Build(out _, out var legacyPath);
        var metas = new[]
        {
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"),
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/web01"),
        };
        await WriteLegacyAsync(legacyPath, metas);

        var count = await library.MigrateFromLegacyAsync(legacyPath);

        Assert.Equal(2, count);
        var migrated = await library.ListAsync();
        Assert.Equal(["appscloud", "web01"], migrated.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        foreach (var meta in metas)
        {
            var match = migrated.Single(c =>
                string.Equals(c.KeyReference, meta.KeyReference, StringComparison.Ordinal));
            Assert.Equal(meta.SecretReference, match.SecretReference);
            Assert.Equal(meta.KeyReference, match.KeyReference);
            Assert.Equal(meta.PrivateKeyPath, match.PrivateKeyPath);
        }
    }

    [Fact]
    public async Task 迁移取资源Id末段作名称_撞名追加序号()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"),
            LegacyMeta("/subscriptions/s2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"),
            LegacyMeta("/subscriptions/s3/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"));

        await library.MigrateFromLegacyAsync(legacyPath);

        Assert.Equal(
            ["appscloud", "appscloud (2)", "appscloud (3)"],
            (await library.ListAsync()).Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 迁移保留旧的SavedAt作为创建与更新时间()
    {
        var library = Build(out _, out var legacyPath);
        var meta = LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud");
        await WriteLegacyAsync(legacyPath, meta);

        await library.MigrateFromLegacyAsync(legacyPath);

        var migrated = (await library.ListAsync()).Single();
        Assert.Equal(meta.SavedAt, migrated.CreatedAt);
        Assert.Equal(meta.SavedAt, migrated.UpdatedAt);
    }

    [Fact]
    public async Task 迁移不把ResourceId写进备注_那等于换个地方存主机关联()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"));

        await library.MigrateFromLegacyAsync(legacyPath);

        var migrated = (await library.ListAsync()).Single();
        Assert.Equal("", migrated.Description);
    }

    [Fact]
    public async Task 迁移后旧文件字节与修改时间均未变()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"));
        var beforeBytes = await File.ReadAllBytesAsync(legacyPath);
        var beforeWrite = File.GetLastWriteTimeUtc(legacyPath);

        await library.MigrateFromLegacyAsync(legacyPath);

        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(legacyPath));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(legacyPath));
    }

    [Fact]
    public async Task 迁移只跑一次_库文件存在即不再读旧文件()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"));
        Assert.Equal(1, await library.MigrateFromLegacyAsync(legacyPath));

        // 旧文件之后被改（例如用户在旧版本里又存了两条）也不该被再次吸收
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/a"),
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/b"),
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/c"));

        Assert.Equal(0, await library.MigrateFromLegacyAsync(legacyPath));
        Assert.Single(await library.ListAsync());
    }

    [Fact]
    public async Task 迁来的凭据被删光后不会复活()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            LegacyMeta("/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud"));
        await library.MigrateFromLegacyAsync(legacyPath);
        var migrated = (await library.ListAsync()).Single();

        await library.DeleteAsync(migrated.Id);
        Assert.Equal(0, await library.MigrateFromLegacyAsync(legacyPath));

        Assert.Empty(await library.ListAsync());
    }

    [Fact]
    public async Task 旧文件为空数组时不创建库文件()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath);

        Assert.Equal(0, await library.MigrateFromLegacyAsync(legacyPath));
        Assert.False(library.Exists());
    }

    [Fact]
    public async Task 旧文件不存在时不创建库文件()
    {
        var library = Build(out _, out var legacyPath);

        Assert.Equal(0, await library.MigrateFromLegacyAsync(legacyPath));
        Assert.False(library.Exists());
    }

    [Fact]
    public async Task 旧文件损坏时显式失败_不创建空的库文件()
    {
        var library = Build(out _, out var legacyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "{ broken");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => library.MigrateFromLegacyAsync(legacyPath));

        Assert.Contains("已损坏", ex.Message);
        Assert.False(library.Exists());
    }

    [Fact]
    public async Task 资源Id末段解析不出时退回用户名()
    {
        var library = Build(out _, out var legacyPath);
        await WriteLegacyAsync(legacyPath,
            new SshCredentialMeta
            {
                ResourceId = "///",
                Username = "rootadmin",
                AuthType = SshAuthType.Password,
                SecretReference = "ssh:x",
                SavedAt = DateTimeOffset.Now,
            });

        await library.MigrateFromLegacyAsync(legacyPath);

        Assert.Equal("rootadmin", (await library.ListAsync()).Single().Name);
    }

    [Fact]
    public async Task 迁移不触碰保险库文件_全程无解密动作()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"));
        var libraryPath = Path.Combine(directory, "ssh-credentials-library.json");
        var legacyPath = Path.Combine(directory, "ssh-credentials.json");
        var vaultPath = Path.Combine(directory, "ssh-vault.dat");

        var vault = new DpapiCredentialVault(vaultPath, NullLogger.Instance);
        await vault.StoreSecretAsync("sshkey:legacy-1", "CANARY-PRIVATE-KEY");
        var vaultBefore = await File.ReadAllBytesAsync(vaultPath);

        var library = new SshCredentialLibrary(libraryPath);
        await WriteLegacyAsync(legacyPath, new SshCredentialMeta
        {
            ResourceId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud",
            AuthType = SshAuthType.PrivateKey,
            SecretReference = null,
            KeyReference = "sshkey:legacy-1",
            SavedAt = DateTimeOffset.Now,
        });

        await library.MigrateFromLegacyAsync(legacyPath);

        // 保险库一个字节都没动 = 迁移路径上没有任何加解密
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(vaultPath));
        Assert.Equal("sshkey:legacy-1", (await library.ListAsync()).Single().KeyReference);
    }

    // ── 按 VM 记住凭据（用户已定口径：每台各记一条） ──────────────

    private const string VmId =
        "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/appscloud";

    private static string OtherVmId => VmId.Replace("appscloud", "hgslt", StringComparison.Ordinal);

    [Fact]
    public async Task 按VM记住凭据后能读回来()
    {
        var library = Build(out _, out _);
        var credential = Credential("apps");
        await library.SaveAsync(credential);

        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        Assert.Equal(credential.Id, await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 没有记录时按VM读返回null()
    {
        var library = Build(out _, out _);

        Assert.Null(await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 按VM记住是幂等的_没变化就不写盘()
    {
        var library = Build(out var libraryPath, out _);
        var credential = Credential("apps");
        await library.SaveAsync(credential);
        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        var bytesBefore = await File.ReadAllBytesAsync(libraryPath);
        var stampBefore = File.GetLastWriteTimeUtc(libraryPath);

        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(libraryPath));
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(libraryPath));
    }

    [Fact]
    public async Task 传null清除该VM的记录()
    {
        var library = Build(out _, out _);
        var credential = Credential("apps");
        await library.SaveAsync(credential);
        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        await library.SetDefaultCredentialIdAsync(VmId, null);

        Assert.Null(await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 不同VM各记各的()
    {
        var library = Build(out _, out _);
        var first = Credential("a");
        var second = Credential("b");
        await library.SaveAsync(first);
        await library.SaveAsync(second);

        await library.SetDefaultCredentialIdAsync(VmId, first.Id);
        await library.SetDefaultCredentialIdAsync(OtherVmId, second.Id);

        Assert.Equal(first.Id, await library.GetDefaultCredentialIdAsync(VmId));
        Assert.Equal(second.Id, await library.GetDefaultCredentialIdAsync(OtherVmId));
    }

    [Fact]
    public async Task ResourceId大小写不同视为同一台机器且不攒成两条记录()
    {
        var library = Build(out var libraryPath, out _);
        var credential = Credential("apps");
        await library.SaveAsync(credential);

        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        Assert.Equal(credential.Id, await library.GetDefaultCredentialIdAsync(VmId.ToUpperInvariant()));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath));
        Assert.Single(document.RootElement.GetProperty("DefaultCredentialByVm").EnumerateObject());
    }

    [Fact]
    public async Task 删除凭据会一并清掉记住它的VM()
    {
        var library = Build(out _, out _);
        var credential = Credential("apps");
        await library.SaveAsync(credential);
        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        await library.DeleteAsync(credential.Id);

        Assert.Null(await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 删除其他凭据不会动记住的VM()
    {
        var library = Build(out _, out _);
        var keep = Credential("keep");
        var drop = Credential("drop");
        await library.SaveAsync(keep);
        await library.SaveAsync(drop);
        await library.SetDefaultCredentialIdAsync(VmId, keep.Id);

        await library.DeleteAsync(drop.Id);

        Assert.Equal(keep.Id, await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 指向已删除凭据的记录在读取时按无记录处理()
    {
        // 手工编辑文件造出的陈旧引用：读取必须回退成"没记住"，而不是返回一个死主键。
        // （正常路径下 DeleteAsync 已经清掉了，所以这条走的是"文件被改过"的情形。）
        // 这里直接手写整份文件，而不是拿 Replace 去改 —— 凭据 Id 在文件里出现两次，
        // 按字符串替换很容易把记录本体也一起改掉，那样引用反而仍指向一条存在的记录，测试就空跑了。
        var library = Build(out var libraryPath, out _);
        var liveCredentialId = Guid.NewGuid();
        var staleCredentialId = Guid.NewGuid();

        await File.WriteAllTextAsync(libraryPath, $$"""
            {
              "SchemaVersion": 2,
              "LastUsedCredentialId": null,
              "DefaultCredentialByVm": { "{{VmId}}": "{{staleCredentialId}}" },
              "Credentials": [
                {
                  "Id": "{{liveCredentialId}}",
                  "Name": "apps",
                  "Username": "rootadmin",
                  "AuthType": "Password",
                  "SecretReference": "ssh:ref-1",
                  "KeyReference": null,
                  "PrivateKeyPath": null,
                  "Description": "",
                  "CreatedAt": "2026-01-01T00:00:00+08:00",
                  "UpdatedAt": "2026-01-01T00:00:00+08:00"
                }
              ]
            }
            """);

        // 反向保护：库里确实有一条凭据、而字典指向的是**另一条不存在的**
        Assert.Equal(liveCredentialId, (await library.ListAsync()).Single().Id);

        Assert.Null(await library.GetDefaultCredentialIdAsync(VmId));
    }

    [Fact]
    public async Task 引用计数只数记住这条凭据的VM()
    {
        var library = Build(out _, out _);
        var used = Credential("used");
        var idle = Credential("idle");
        await library.SaveAsync(used);
        await library.SaveAsync(idle);

        await library.SetDefaultCredentialIdAsync(VmId, used.Id);
        await library.SetDefaultCredentialIdAsync(OtherVmId, used.Id);

        Assert.Equal(2, await library.CountVmsUsingAsync(used.Id));
        Assert.Equal(0, await library.CountVmsUsingAsync(idle.Id));
    }

    // ── schema 版本号 ─────────────────────────────────────────────

    [Fact]
    public async Task 新建的库文件带当前版本号()
    {
        var library = Build(out var libraryPath, out _);
        await library.SaveAsync(Credential("apps"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath));
        Assert.Equal(
            SshCredentialLibraryDocument.CurrentSchemaVersion,
            document.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task 旧的v1文件仍可读_但回写时升为当前版本号()
    {
        // 低版本文件读进来是允许的，但**回写必须盖上当前版本号** —— 否则会留下
        // "内容已是新版、版本号还写着旧版"的错配，而版本号正是用来拦住旧程序的：
        // 它看到低版本号会照常读写，然后把读不懂的新字段静默丢掉。
        var library = Build(out var libraryPath, out _);
        await File.WriteAllTextAsync(libraryPath,
            """{"SchemaVersion":1,"LastUsedCredentialId":null,"Credentials":[]}""");

        var credential = Credential("apps");
        await library.SaveAsync(credential);
        await library.SetDefaultCredentialIdAsync(VmId, credential.Id);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath));
        Assert.Equal(
            SshCredentialLibraryDocument.CurrentSchemaVersion,
            document.RootElement.GetProperty("SchemaVersion").GetInt32());

        // 新字段也真的落盘了 —— 否则"升版本"只是个空口号
        Assert.Equal(
            credential.Id.ToString(),
            document.RootElement.GetProperty("DefaultCredentialByVm").EnumerateObject().Single().Value.GetString());
    }
}
