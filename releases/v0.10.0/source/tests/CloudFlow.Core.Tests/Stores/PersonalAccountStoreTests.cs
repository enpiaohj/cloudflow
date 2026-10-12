using CloudFlow.Core.Identity;
using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

/// <summary>
/// 个人 Microsoft 账户元数据注册表（Embedded Azure CLI 身份）：
/// 只保存非敏感标识，用于在账户列表与顶栏中恢复个人账户；
/// 不保存 Token、Refresh Token、密码或 CLI Cache 内容。
/// </summary>
public sealed class PersonalAccountStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "personal-accounts.json");

    private static CloudAccount Account(
        string profileId,
        string username = "personal@outlook.com",
        string displayName = "personal") => new()
        {
            AccountId = "azurecli:" + profileId,
            Username = username,
            DisplayName = displayName,
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = profileId
        };

    [Fact]
    public void Save_新实例可恢复个人账户元数据()
    {
        const string profileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        new PersonalAccountStore(FilePath).Save(Account(profileId));

        var restored = new PersonalAccountStore(FilePath).Load().Single();

        Assert.Equal("azurecli:" + profileId, restored.AccountId);
        Assert.Equal("personal@outlook.com", restored.Username);
        Assert.Equal("personal", restored.DisplayName);
        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, restored.ProviderType);
        Assert.Equal(profileId, restored.ProviderProfileId);
    }

    [Fact]
    public void Save_同一账户重复保存只保留一条并更新显示信息()
    {
        const string profileId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var store = new PersonalAccountStore(FilePath);
        store.Save(Account(profileId));
        store.Save(Account(profileId, "renamed@outlook.com", "renamed"));

        var accounts = new PersonalAccountStore(FilePath).Load();

        Assert.Single(accounts);
        Assert.Equal("renamed@outlook.com", accounts[0].Username);
    }

    [Fact]
    public void Remove_仅移除目标账户()
    {
        var store = new PersonalAccountStore(FilePath);
        store.Save(Account("cccccccccccccccccccccccccccccccc"));
        store.Save(Account("dddddddddddddddddddddddddddddddd"));

        store.Remove("azurecli:cccccccccccccccccccccccccccccccc");

        var remaining = new PersonalAccountStore(FilePath).Load();
        Assert.Single(remaining);
        Assert.Equal("azurecli:dddddddddddddddddddddddddddddddd", remaining[0].AccountId);
    }

    [Fact]
    public void Remove_账户不存在时不抛异常()
    {
        var store = new PersonalAccountStore(FilePath);

        store.Remove("azurecli:missing");
    }

    [Fact]
    public void Load_文件不存在返回空列表()
    {
        Assert.Empty(new PersonalAccountStore(FilePath).Load());
    }

    [Fact]
    public void Load_损坏文件返回空列表()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Empty(new PersonalAccountStore(FilePath).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
