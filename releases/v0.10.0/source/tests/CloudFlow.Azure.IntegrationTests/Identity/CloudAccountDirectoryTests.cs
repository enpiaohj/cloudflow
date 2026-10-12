using CloudFlow.Core.Identity;
using CloudFlow.Azure.Identity;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity;

/// <summary>
/// 账户目录：把 MSAL 企业账户与个人 Microsoft 账户（Embedded Azure CLI）
/// 合并为统一列表，并按 ProviderType 解析应使用的身份 Provider。
/// 上层 UI 只依赖本服务，不再自行判断账户类型。
/// </summary>
public sealed class CloudAccountDirectoryTests
{
    private sealed class FakeProvider(AuthenticationProviderType type) : ICloudIdentityProvider
    {
        public AuthenticationProviderType Type => type;

        public int SignOutCalls { get; private set; }

        public string? SignedOutAccountId { get; private set; }

        public Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("账户目录测试不涉及登录");

        public Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
            CloudAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubscriptionProfile>>([]);

        public Task<CloudAccessToken> GetCredentialAsync(
            CloudCredentialContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("账户目录测试不涉及凭据");

        public Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default)
        {
            SignOutCalls++;
            SignedOutAccountId = account.AccountId;
            return Task.CompletedTask;
        }
    }

    /// <summary>支持登录进度回调的个人账户 Provider 替身。</summary>
    private sealed class FakePersonalProvider : ICloudIdentityProvider, IInteractiveSignInProgress
    {
        public AuthenticationProviderType Type => AuthenticationProviderType.EmbeddedAzureCli;

        public CloudAccount? NextAccount { get; set; }

        public List<string> Messages { get; } = [];

        public Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default) =>
            SignInAsync(_ => { }, cancellationToken);

        public Task<CloudAccount> SignInAsync(
            Action<string> onSignInMessage, CancellationToken cancellationToken = default)
        {
            onSignInMessage("To sign in, use a web browser and enter the code ABC123 to authenticate.");
            return Task.FromResult(NextAccount ?? throw new InvalidOperationException("未设置登录结果"));
        }

        public Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
            CloudAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubscriptionProfile>>([]);

        public Task<CloudAccessToken> GetCredentialAsync(
            CloudCredentialContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSessions(IReadOnlyList<CloudAccount> accounts) : IAccountSessionManager
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken ct = default) =>
            Task.FromResult(accounts);

        public Task<CloudAccount> AddAccountAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAccountAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AccountSession?> GetSessionAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<AccountSession?>(null);

        public Task<AccountSession?> TryRestoreSessionAsync(string? preferredAccountId = null, CancellationToken ct = default) =>
            Task.FromResult<AccountSession?>(null);

        public Task<IReadOnlyList<TenantProfile>> GetTenantsAsync(CloudAccount account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantProfile>>([]);
    }

    private sealed class FakeRegistry : IPersonalAccountRegistry
    {
        public List<CloudAccount> Accounts { get; } = [];

        public IReadOnlyList<CloudAccount> Load() => [.. Accounts];

        public void Save(CloudAccount account)
        {
            Accounts.RemoveAll(item => item.AccountId == account.AccountId);
            Accounts.Add(account);
        }

        public void Remove(string accountId) =>
            Accounts.RemoveAll(item => item.AccountId == accountId);
    }

    private static CloudAccount EntraAccount(string id) => new()
    {
        AccountId = id,
        Username = id + "@company.com",
        DisplayName = "企业账户",
        ProviderType = AuthenticationProviderType.EntraMsal
    };

    private static CloudAccount PersonalAccount(string profileId) => new()
    {
        AccountId = "azurecli:" + profileId,
        Username = "personal@outlook.com",
        DisplayName = "personal",
        ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
        ProviderProfileId = profileId
    };

    private static (CloudAccountDirectory directory, FakeRegistry registry) Build(
        IReadOnlyList<CloudAccount> entraAccounts)
    {
        var registry = new FakeRegistry();
        var directory = new CloudAccountDirectory(
            [new FakeProvider(AuthenticationProviderType.EntraMsal),
             new FakeProvider(AuthenticationProviderType.EmbeddedAzureCli)],
            new FakeSessions(entraAccounts),
            registry);
        return (directory, registry);
    }

    [Fact]
    public async Task GetAccounts_合并企业账户与个人账户()
    {
        var (directory, registry) = Build([EntraAccount("entra-1")]);
        registry.Save(PersonalAccount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var accounts = await directory.GetAccountsAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a.ProviderType == AuthenticationProviderType.EntraMsal);
        Assert.Contains(accounts, a => a.ProviderType == AuthenticationProviderType.EmbeddedAzureCli);
    }

    [Fact]
    public async Task GetAccounts_无个人账户时仅返回企业账户()
    {
        var (directory, _) = Build([EntraAccount("entra-1")]);

        var accounts = await directory.GetAccountsAsync();

        Assert.Single(accounts);
        Assert.Equal("entra-1", accounts[0].AccountId);
    }

    [Fact]
    public async Task GetAccounts_同一账户不重复出现()
    {
        var duplicate = EntraAccount("entra-1");
        var (directory, _) = Build([duplicate, duplicate]);

        var accounts = await directory.GetAccountsAsync();

        Assert.Single(accounts);
    }

    [Fact]
    public void ResolveProvider_按ProviderType返回对应Provider()
    {
        var (directory, _) = Build([]);

        Assert.Equal(
            AuthenticationProviderType.EmbeddedAzureCli,
            directory.ResolveProvider(PersonalAccount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).Type);
        Assert.Equal(
            AuthenticationProviderType.EntraMsal,
            directory.ResolveProvider(EntraAccount("entra-1")).Type);
    }

    [Fact]
    public void ResolveProvider_未注册类型时明确拒绝()
    {
        var (directory, _) = Build([]);
        var account = new CloudAccount
        {
            AccountId = "unknown",
            Username = "x@y.z",
            ProviderType = (AuthenticationProviderType)999
        };

        Assert.Throws<NotSupportedException>(() => directory.ResolveProvider(account));
    }

    [Fact]
    public void RememberPersonalAccount_写入注册表并可按账户查询()
    {
        var (directory, registry) = Build([]);
        var account = PersonalAccount("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        directory.RememberPersonalAccount(account);

        Assert.Contains(registry.Accounts, item => item.AccountId == account.AccountId);
    }

    [Fact]
    public void ForgetAccount_移除目标账户且保留其他账户()
    {
        var (directory, registry) = Build([]);
        registry.Save(PersonalAccount("cccccccccccccccccccccccccccccccc"));
        registry.Save(PersonalAccount("dddddddddddddddddddddddddddddddd"));

        directory.ForgetAccount("azurecli:cccccccccccccccccccccccccccccccc");

        Assert.Single(registry.Accounts);
        Assert.Equal("azurecli:dddddddddddddddddddddddddddddddd", registry.Accounts[0].AccountId);
    }

    [Fact]
    public void ForgetAccount_企业账户不由注册表移除()
    {
        var (directory, registry) = Build([EntraAccount("entra-1")]);

        directory.ForgetAccount("entra-1");

        Assert.Empty(registry.Accounts);
    }

    [Fact]
    public async Task SignInPersonalAccount_登录提示可回调并登记账户()
    {
        var provider = new FakePersonalProvider
        {
            NextAccount = PersonalAccount("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
        };
        var registry = new FakeRegistry();
        var directory = new CloudAccountDirectory(
            [provider], new FakeSessions([]), registry);
        var messages = new List<string>();

        var account = await directory.SignInPersonalAccountAsync(messages.Add);

        Assert.Contains(messages, message => message.Contains("ABC123"));
        Assert.Contains(registry.Accounts, item => item.AccountId == account.AccountId);
    }

    [Fact]
    public async Task SignInPersonalAccount_未注册个人Provider时明确拒绝()
    {
        var (directory, _) = Build([]);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            directory.SignInPersonalAccountAsync(_ => { }));
    }

    [Fact]
    public async Task RemoveAccount_个人账户经Provider登出并清理注册表()
    {
        var provider = new FakePersonalProvider();
        var registry = new FakeRegistry();
        var directory = new CloudAccountDirectory([provider], new FakeSessions([]), registry);
        var account = PersonalAccount("ffffffffffffffffffffffffffffffff");
        registry.Save(account);

        await directory.RemoveAccountAsync(account);

        Assert.Empty(registry.Accounts);
    }

    [Fact]
    public async Task RemoveAccount_企业账户经Provider登出()
    {
        var msal = new FakeProvider(AuthenticationProviderType.EntraMsal);
        var registry = new FakeRegistry();
        var directory = new CloudAccountDirectory([msal], new FakeSessions([]), registry);
        var account = EntraAccount("entra-1");

        await directory.RemoveAccountAsync(account);

        Assert.Equal(1, msal.SignOutCalls);
        Assert.Equal("entra-1", msal.SignedOutAccountId);
    }
}
