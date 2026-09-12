using CloudFlow.Core.Identity;
using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// EmbeddedAzureCliIdentityProvider 契约测试（P0 Spike 步骤 5）。
/// 以 Fake Runner / Fake ProfileManager 验证：登录建 Profile、失败清理、
/// 订阅解析、Token 获取的资源映射、ProviderType 校验、登出不删 Profile。
/// </summary>
public sealed class EmbeddedAzureCliIdentityProviderTests
{
    private sealed class FakeRunner : IAzureCliProcessRunner
    {
        public AzureCliInvocation? LastInvocation { get; private set; }
        public Func<AzureCliInvocation, AzureCliResult> Responder { get; set; } =
            invocation => new AzureCliResult(0, "{}", "");

        public Task<AzureCliResult> RunAsync(AzureCliInvocation invocation, CancellationToken cancellationToken = default)
        {
            LastInvocation = invocation;
            return Task.FromResult(Responder(invocation));
        }
    }

    private sealed class FakeProfiles : IAzureCliProfileManager
    {
        public List<string> Created { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> LoggedOut { get; } = [];

        public string CreateProfile()
        {
            var id = Guid.NewGuid().ToString("N");
            Created.Add(id);
            return id;
        }

        public IReadOnlyList<string> GetProfileIds() => Created.Except(Deleted).ToList();

        public string GetProfilePath(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId) || !Guid.TryParseExact(profileId, "N", out _))
            {
                throw new ArgumentException("ProfileId 必须是 N 格式 GUID。", nameof(profileId));
            }
            return Path.Combine(Path.GetTempPath(), "cli-profile-" + profileId);
        }

        public void DeleteProfile(string profileId) => Deleted.Add(profileId);

        public Task<AzureCliResult> LogoutAsync(string profileId, CancellationToken cancellationToken = default)
        {
            LoggedOut.Add(profileId);
            return Task.FromResult(new AzureCliResult(0, "", ""));
        }
    }

    private const string LoginOutput = """
        [
          {
            "id": "11111111-2222-3333-4444-555555555555",
            "name": "Azure subscription 1",
            "state": "Enabled",
            "user": { "name": "personal@outlook.com", "type": "user" },
            "tenantId": "aaaa-bbbb-cccc"
          },
          {
            "id": "66666666-7777-8888-9999-000000000000",
            "name": "Disabled Sub",
            "state": "Disabled",
            "user": { "name": "personal@outlook.com", "type": "user" },
            "tenantId": "dddd-eeee-ffff"
          }
        ]
        """;

    private const string AccessTokenOutput = """
        { "accessToken": "cli-arm-token-value", "tokenType": "Bearer", "expiresIn": 3600 }
        """;

    private static AzureCliInvocation LastArgs(FakeRunner runner) =>
        runner.LastInvocation ?? throw new InvalidOperationException("未发起 CLI 调用");

    [Fact]
    public void Type_标识为EmbeddedAzureCli()
    {
        var provider = new EmbeddedAzureCliIdentityProvider(new FakeRunner(), new FakeProfiles(), "C:\\rt\\az.cmd");

        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, provider.Type);
    }

    [Fact]
    public async Task SignIn_创建独立Profile并以它执行login()
    {
        var runner = new FakeRunner { Responder = _ => new AzureCliResult(0, LoginOutput, "") };
        var profiles = new FakeProfiles();
        var provider = new EmbeddedAzureCliIdentityProvider(runner, profiles, "C:\\rt\\az.cmd");

        var account = await provider.SignInAsync();

        var invocation = LastArgs(runner);
        Assert.Contains("login", invocation.Arguments);
        Assert.Equal(profiles.GetProfilePath(profiles.Created.Single()), invocation.ConfigDirectory);

        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, account.ProviderType);
        Assert.Equal("personal@outlook.com", account.Username);
        Assert.Equal(profiles.Created.Single(), account.ProviderProfileId);
        Assert.StartsWith("azurecli:", account.AccountId);
    }

    [Fact]
    public async Task SignIn_登录失败时清理Profile并抛脱敏异常()
    {
        var runner = new FakeRunner { Responder = _ => new AzureCliResult(1, "", "ERROR: login failed") };
        var profiles = new FakeProfiles();
        var provider = new EmbeddedAzureCliIdentityProvider(runner, profiles, "C:\\rt\\az.cmd");

        await Assert.ThrowsAsync<AzureCliException>(() => provider.SignInAsync());

        var failedProfileId = profiles.Created.Single();
        Assert.Contains(failedProfileId, profiles.Deleted);
    }

    [Fact]
    public async Task GetSubscriptions_解析账号订阅列表()
    {
        var runner = new FakeRunner { Responder = _ => new AzureCliResult(0, LoginOutput, "") };
        var profiles = new FakeProfiles();
        var provider = new EmbeddedAzureCliIdentityProvider(runner, profiles, "C:\\rt\\az.cmd");
        var account = await provider.SignInAsync();

        var subscriptions = await provider.GetSubscriptionsAsync(account);

        var invocation = LastArgs(runner);
        Assert.Contains("account", invocation.Arguments);
        Assert.Contains("list", invocation.Arguments);
        Assert.Equal(2, subscriptions.Count);
        Assert.Contains(subscriptions, s =>
            s.SubscriptionId == "11111111-2222-3333-4444-555555555555" &&
            s.DisplayName == "Azure subscription 1" &&
            s.TenantId == "aaaa-bbbb-cccc" &&
            s.State == "Enabled");
        Assert.Contains(subscriptions, s => s.State == "Disabled");
    }

    [Fact]
    public async Task GetSubscriptions_ProfileId非法时拒绝()
    {
        var provider = new EmbeddedAzureCliIdentityProvider(new FakeRunner(), new FakeProfiles(), "C:\\rt\\az.cmd");
        var account = new CloudAccount
        {
            AccountId = "azurecli:bad",
            Username = "x@y.z",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "../evil"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetSubscriptionsAsync(account));
    }

    [Fact]
    public async Task GetCredential_映射资源并解析Token()
    {
        var runner = new FakeRunner { Responder = _ => new AzureCliResult(0, AccessTokenOutput, "") };
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "azurecli:abc",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        var credential = await provider.GetCredentialAsync(context);
        var token = await credential.GetAsync(["https://management.azure.com/.default"], CancellationToken.None);

        var invocation = LastArgs(runner);
        Assert.Contains("get-access-token", invocation.Arguments);
        Assert.Contains("--resource", invocation.Arguments);
        Assert.Contains("https://management.azure.com", invocation.Arguments);
        Assert.Contains("--tenant", invocation.Arguments);
        Assert.Contains("tenant-1", invocation.Arguments);
        Assert.Equal("cli-arm-token-value", token);
    }

    [Fact]
    public async Task GetCredential_ProviderType不匹配时拒绝()
    {
        var provider = new EmbeddedAzureCliIdentityProvider(new FakeRunner(), new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "entra-1",
            TenantId = "t",
            SubscriptionId = "s",
            ProviderType = AuthenticationProviderType.EntraMsal
        };

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetCredentialAsync(context));
    }

    [Fact]
    public async Task SignOut_执行logout但不删除Profile()
    {
        var profiles = new FakeProfiles();
        var provider = new EmbeddedAzureCliIdentityProvider(new FakeRunner(), profiles, "C:\\rt\\az.cmd");
        var account = new CloudAccount
        {
            AccountId = "azurecli:abc",
            Username = "x@y.z",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        await provider.SignOutAsync(account);

        Assert.Equal([account.ProviderProfileId!], profiles.LoggedOut);
        Assert.DoesNotContain(profiles.Created, profiles.Deleted.Contains);
    }
}
