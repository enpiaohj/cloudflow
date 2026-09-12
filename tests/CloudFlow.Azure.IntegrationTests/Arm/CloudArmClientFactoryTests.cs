using CloudFlow.Core.Identity;
using CloudFlow.Azure.Arm;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Arm;

/// <summary>
/// CloudArmClientFactory 路由测试（P0 Spike 步骤 6，规范 §十四）：
/// 按 CloudAccount.ProviderType 找对应 Provider → CloudAccessToken → ArmClient。
/// </summary>
public sealed class CloudArmClientFactoryTests
{
    private sealed class FakeProvider(AuthenticationProviderType type) : ICloudIdentityProvider
    {
        public AuthenticationProviderType Type => type;

        public CloudCredentialContext? LastContext { get; private set; }

        public Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("工厂测试不涉及登录");

        public Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
            CloudAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubscriptionProfile>>([]);

        public Task<CloudAccessToken> GetCredentialAsync(
            CloudCredentialContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(new CloudAccessToken
            {
                AcquireAsync = (scopes, ct) => Task.FromResult("factory-test-token")
            });
        }

        public Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static CloudAccount NewAccount(AuthenticationProviderType type, string profileId) => new()
    {
        AccountId = "acc-" + type,
        Username = "user@example.com",
        ProviderType = type,
        ProviderProfileId = profileId
    };

    [Fact]
    public async Task Create_按ProviderType路由并绑定完整凭据上下文()
    {
        var msal = new FakeProvider(AuthenticationProviderType.EntraMsal);
        var cli = new FakeProvider(AuthenticationProviderType.EmbeddedAzureCli);
        var factory = new CloudArmClientFactory([msal, cli]);
        var account = NewAccount(AuthenticationProviderType.EmbeddedAzureCli, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var client = await factory.CreateAsync(account, "tenant-1");

        Assert.NotNull(client);
        Assert.NotNull(cli.LastContext);
        Assert.Equal(account.AccountId, cli.LastContext.AccountId);
        Assert.Equal("tenant-1", cli.LastContext.TenantId);
        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, cli.LastContext.ProviderType);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", cli.LastContext.ProviderProfileId);
        Assert.Null(msal.LastContext);
    }

    [Fact]
    public async Task Create_未知ProviderType时明确拒绝()
    {
        var factory = new CloudArmClientFactory(Array.Empty<ICloudIdentityProvider>());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            factory.CreateAsync(NewAccount((AuthenticationProviderType)999, "b"), "t"));
    }
}
