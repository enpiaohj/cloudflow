using System.IO;
using CloudFlow.Terminal.Security;
using Xunit;

namespace CloudFlow.Terminal.Tests.Security;

/// <summary>
/// SSH 凭据元数据存储：文件里只有引用键与用户名等非秘密；
/// 明文（密码/私钥内容）在 DpapiCredentialVault，两者通过引用键关联。
/// </summary>
public sealed class SshCredentialStoreTests
{
    private static (SshCredentialStore Store, string Path) Build()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"),
            "ssh-credentials.json");
        return (new SshCredentialStore(path), path);
    }

    private static SshCredentialMeta Meta(string resourceId = "/subscriptions/s/virtualMachines/vm1") => new()
    {
        ResourceId = resourceId,
        Username = "azureuser",
        AuthType = SshAuthType.PrivateKey,
        SecretReference = "ssh:ref-1",
        KeyReference = "sshkey:ref-2",
        PrivateKeyPath = @"C:\keys\deploy",
        SavedAt = DateTimeOffset.Now
    };

    [Fact]
    public async Task 保存后按ResourceId查到()
    {
        var (store, _) = Build();
        var meta = Meta();

        await store.SaveAsync(meta);

        var found = await store.FindAsync(meta.ResourceId);
        Assert.NotNull(found);
        Assert.Equal("azureuser", found.Username);
        Assert.Equal(SshAuthType.PrivateKey, found.AuthType);
        Assert.Equal("ssh:ref-1", found.SecretReference);
        Assert.Equal("sshkey:ref-2", found.KeyReference);
    }

    [Fact]
    public async Task 未保存的VM查不到_返回null()
    {
        var (store, _) = Build();

        Assert.Null(await store.FindAsync("/subscriptions/s/virtualMachines/nope"));
    }

    [Fact]
    public async Task 覆盖保存_同VM只留一条()
    {
        var (store, _) = Build();
        await store.SaveAsync(Meta());
        await store.SaveAsync(new SshCredentialMeta
        {
            ResourceId = "/subscriptions/s/virtualMachines/vm1",
            Username = "admin",
            AuthType = SshAuthType.Password,
            SecretReference = "ssh:ref-new",
            SavedAt = DateTimeOffset.Now
        });

        var found = await store.FindAsync("/subscriptions/s/virtualMachines/vm1");
        Assert.Equal("admin", found!.Username);
        Assert.Equal("ssh:ref-new", found.SecretReference);
    }

    [Fact]
    public async Task 删除后查不到()
    {
        var (store, _) = Build();
        await store.SaveAsync(Meta());

        await store.DeleteAsync("/subscriptions/s/virtualMachines/vm1");

        Assert.Null(await store.FindAsync("/subscriptions/s/virtualMachines/vm1"));
    }
}
