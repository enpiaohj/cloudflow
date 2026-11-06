using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Azure.Identity.Msal;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.Msal;

/// <summary>
/// MsalIdentityProvider 行为测试（P0 Spike 步骤 4）。
/// 以手写 Fake 验证 Provider 契约：类型标识、登录/发现/登出委托、
/// 会话失效转 ReauthenticationRequiredException、Token 回调可用。
/// 不依赖真实 MSAL / Azure。
/// </summary>
public sealed class MsalIdentityProviderTests
{
    private sealed class FakeSessionManager : IAccountSessionManager
    {
        public bool IsConfigured { get; set; } = true;

        public CloudAccount? NextSignInAccount { get; set; }
        public int SignInCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public string? RemovedAccountId { get; private set; }
        public string? SessionForAccountId { get; private set; }
        public bool HasSession { get; set; } = true;

        public Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CloudAccount>>([]);

        public Task<CloudAccount> AddAccountAsync(CancellationToken ct = default)
        {
            SignInCalls++;
            return Task.FromResult(NextSignInAccount ?? throw new InvalidOperationException("未设置登录结果"));
        }

        public Task RemoveAccountAsync(string accountId, CancellationToken ct = default)
        {
            RemoveCalls++;
            RemovedAccountId = accountId;
            return Task.CompletedTask;
        }

        public Task<AccountSession?> GetSessionAsync(string accountId, CancellationToken ct = default)
        {
            SessionForAccountId = accountId;
            if (!HasSession)
            {
                return Task.FromResult<AccountSession?>(null);
            }

            var account = new CloudAccount
            {
                AccountId = accountId,
                Username = "user@company.com",
                ProviderType = AuthenticationProviderType.EntraMsal
            };
            return Task.FromResult<AccountSession?>(new AccountSession
            {
                Account = account,
                AccessTokenProvider = (scopes, token) => Task.FromResult("fake-arm-token")
            });
        }

        public Task<AccountSession?> TryRestoreSessionAsync(string? preferredAccountId = null, CancellationToken ct = default) =>
            Task.FromResult<AccountSession?>(null);

        public Task<IReadOnlyList<TenantProfile>> GetTenantsAsync(CloudAccount account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantProfile>>([]);
    }

    private sealed class FakeDiscovery : ISubscriptionDiscoveryService
    {
        public string? DiscoveredForSession { get; private set; }

        public Task<IReadOnlyList<SubscriptionProfile>> DiscoverAsync(AccountSession session, CancellationToken ct = default)
        {
            DiscoveredForSession = session.Account.AccountId;
            return Task.FromResult<IReadOnlyList<SubscriptionProfile>>(
            [
                new SubscriptionProfile
                {
                    SubscriptionId = "sub-1",
                    DisplayName = "Sub One",
                    TenantId = "tenant-1",
                    State = "Enabled"
                }
            ]);
        }
    }

    private static CloudAccount NewAccount() => new()
    {
        AccountId = "entra-1",
        Username = "user@company.com",
        DisplayName = "User",
        ProviderType = AuthenticationProviderType.EntraMsal
    };

    [Fact]
    public void Type_标识为EntraMsal()
    {
        var provider = new MsalIdentityProvider(new FakeSessionManager(), new FakeDiscovery());

        Assert.Equal(AuthenticationProviderType.EntraMsal, provider.Type);
    }

    [Fact]
    public async Task SignIn_委托会话管理器完成交互登录()
    {
        var sessions = new FakeSessionManager { NextSignInAccount = NewAccount() };
        var provider = new MsalIdentityProvider(sessions, new FakeDiscovery());

        var account = await provider.SignInAsync();

        Assert.Equal(1, sessions.SignInCalls);
        Assert.Equal("entra-1", account.AccountId);
    }

    [Fact]
    public async Task GetSubscriptions_按账户获取会话并委托发现()
    {
        var sessions = new FakeSessionManager();
        var discovery = new FakeDiscovery();
        var provider = new MsalIdentityProvider(sessions, discovery);

        var subscriptions = await provider.GetSubscriptionsAsync(NewAccount());

        Assert.Equal("entra-1", sessions.SessionForAccountId);
        Assert.Equal("entra-1", discovery.DiscoveredForSession);
        Assert.Single(subscriptions);
        Assert.Equal("sub-1", subscriptions[0].SubscriptionId);
    }

    [Fact]
    public async Task GetSubscriptions_会话失效时要求重新登录()
    {
        var sessions = new FakeSessionManager { HasSession = false };
        var provider = new MsalIdentityProvider(sessions, new FakeDiscovery());

        var exception = await Assert.ThrowsAsync<ReauthenticationRequiredException>(() =>
            provider.GetSubscriptionsAsync(NewAccount()));

        Assert.Contains("重新登录", exception.Message);
    }

    [Fact]
    public async Task GetCredential_返回可用Token回调()
    {
        var sessions = new FakeSessionManager();
        var provider = new MsalIdentityProvider(sessions, new FakeDiscovery());
        var context = new CloudCredentialContext
        {
            AccountId = "entra-1",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EntraMsal
        };

        var credential = await provider.GetCredentialAsync(context);
        var token = await credential.AcquireAsync(["https://management.azure.com/.default"], CancellationToken.None);

        Assert.Equal("entra-1", sessions.SessionForAccountId);
        Assert.Equal("fake-arm-token", token);
    }

    [Fact]
    public async Task SignOut_仅移除指定账户()
    {
        var sessions = new FakeSessionManager();
        var provider = new MsalIdentityProvider(sessions, new FakeDiscovery());

        await provider.SignOutAsync(NewAccount());

        Assert.Equal(1, sessions.RemoveCalls);
        Assert.Equal("entra-1", sessions.RemovedAccountId);
    }
}
