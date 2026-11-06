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
    public void 构造时不解析Runtime_避免未使用个人登录时阻塞应用启动()
    {
        var provider = new EmbeddedAzureCliIdentityProvider(
            new FakeRunner(),
            new FakeProfiles(),
            new AzureCliRuntimeManager(new FakeRunner(), probePaths: ["C:\\missing\\az.cmd"]));

        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, provider.Type);
    }

    [Fact]
    public async Task SignIn_设备码流回调登录提示并解析账户()
    {
        var runner = new FakeRunner
        {
            Responder = invocation =>
            {
                invocation.OnOutputLine?.Invoke(
                    "To sign in, use a web browser to open the page https://login.microsoft.com/device and enter the code N3VSGZNUY to authenticate.");
                return new AzureCliResult(0, LoginOutput, "");
            }
        };
        var profiles = new FakeProfiles();
        var provider = new EmbeddedAzureCliIdentityProvider(runner, profiles, "C:\\rt\\az.cmd");
        var messages = new List<string>();

        var account = await provider.SignInAsync(messages.Add);

        var invocation = LastArgs(runner);
        Assert.Contains("login", invocation.Arguments);
        Assert.Contains("--use-device-code", invocation.Arguments);
        Assert.Equal(profiles.GetProfilePath(profiles.Created.Single()), invocation.ConfigDirectory);
        Assert.Contains(messages, message => message.Contains("N3VSGZNUY"));

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
            AccountId = "azurecli:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
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
    public async Task GetCredential_网络瞬断时自动重试并在恢复后成功()
    {
        // 前两次返回真实的瞬断 stderr（连接被重置），第三次成功——模拟网波动一下又恢复。
        var calls = 0;
        var runner = new FakeRunner
        {
            Responder = _ => ++calls < 3
                ? new AzureCliResult(1, "", "ConnectionResetError: [WinError 10054] 远程主机强迫关闭了一个现有的连接。")
                : new AzureCliResult(0, AccessTokenOutput, "")
        };
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "azurecli:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        var credential = await provider.GetCredentialAsync(context);
        var token = await credential.GetAsync(["https://management.azure.com/.default"], CancellationToken.None);

        Assert.Equal("cli-arm-token-value", token);
        Assert.True(calls == 3, $"应重试到第 3 次成功，实际调用 {calls} 次。");
    }

    [Fact]
    public async Task GetCredential_非网络失败不重试_且标记为不可重试()
    {
        var calls = 0;
        var runner = new FakeRunner
        {
            Responder = _ =>
            {
                calls++;
                return new AzureCliResult(1, "", "ERROR: (AuthorizationFailed) denied");
            }
        };
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "azurecli:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var credential = await provider.GetCredentialAsync(context);

        var ex = await Assert.ThrowsAsync<AzureCliException>(
            () => credential.GetAsync(["https://management.azure.com/.default"], CancellationToken.None));

        Assert.False(ex.IsTransientNetwork);
        Assert.Equal(1, calls); // 权限类失败不该触发重试
        Assert.Contains("AuthorizationFailed", ex.Message);
    }

    [Fact]
    public async Task GetCredential_网络瞬断最终仍失败时_标记为可重试()
    {
        var calls = 0;
        var runner = new FakeRunner
        {
            Responder = _ =>
            {
                calls++;
                return new AzureCliResult(1, "", "ConnectionResetError: [WinError 10054] 远程主机强迫关闭。");
            }
        };
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "azurecli:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var credential = await provider.GetCredentialAsync(context);

        var ex = await Assert.ThrowsAsync<AzureCliException>(
            () => credential.GetAsync(["https://management.azure.com/.default"], CancellationToken.None));

        Assert.True(ex.IsTransientNetwork);
        Assert.True(calls == 3, $"耗尽重试次数后抛出，实际调用 {calls} 次。");
        Assert.Contains("网络连接中断", ex.Message);
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
    public async Task GetCredential_AccountId与ProfileId不一致时拒绝()
    {
        var provider = new EmbeddedAzureCliIdentityProvider(new FakeRunner(), new FakeProfiles(), "C:\\rt\\az.cmd");
        var context = new CloudCredentialContext
        {
            AccountId = "azurecli:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
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

    // ---- 令牌缓存与多账户隔离 ----

    private const string Arm = "https://management.azure.com/.default";

    private static CloudCredentialContext Ctx(string profileId, string tenant = "tenant-1") => new()
    {
        AccountId = "azurecli:" + profileId,
        TenantId = tenant,
        SubscriptionId = "sub-1",
        ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
        ProviderProfileId = profileId
    };

    /// <summary>令牌内容 = 调用序号 + profile 目录，便于断言"拿到的是谁的令牌"。</summary>
    private static FakeRunner CountingRunner(out Func<int> calls)
    {
        var count = 0;
        calls = () => Volatile.Read(ref count);
        return new FakeRunner
        {
            Responder = inv =>
            {
                var n = Interlocked.Increment(ref count);
                var profile = Path.GetFileName(inv.ConfigDirectory ?? "");
                var tenant = inv.Arguments[Array.IndexOf(inv.Arguments.ToArray(), "--tenant") + 1];
                return new AzureCliResult(0,
                    $"{{ \"accessToken\": \"tok-{n}-{profile}-{tenant}\", \"expires_on\": {DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()} }}", "");
            }
        };
    }

    private const string ProfileA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProfileB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task 令牌缓存_同账户同租户重复获取只调用一次CLI()
    {
        var runner = CountingRunner(out var calls);
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");
        var credential = await provider.GetCredentialAsync(Ctx(ProfileA));

        var t1 = await credential.GetAsync([Arm]);
        var t2 = await (await provider.GetCredentialAsync(Ctx(ProfileA))).GetAsync([Arm]);

        Assert.Equal(t1, t2);
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task 令牌缓存_不同账户互不串用()
    {
        var runner = CountingRunner(out var calls);
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");

        var a = await (await provider.GetCredentialAsync(Ctx(ProfileA))).GetAsync([Arm]);
        var b = await (await provider.GetCredentialAsync(Ctx(ProfileB))).GetAsync([Arm]);
        var a2 = await (await provider.GetCredentialAsync(Ctx(ProfileA))).GetAsync([Arm]);

        Assert.Contains(ProfileA, a);
        Assert.Contains(ProfileB, b);
        Assert.NotEqual(a, b);
        Assert.Equal(a, a2);
        Assert.Equal(2, calls());
    }

    [Fact]
    public async Task 令牌缓存_同账户不同租户分别缓存()
    {
        var runner = CountingRunner(out var calls);
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");

        var t1 = await (await provider.GetCredentialAsync(Ctx(ProfileA, "tenant-1"))).GetAsync([Arm]);
        var t2 = await (await provider.GetCredentialAsync(Ctx(ProfileA, "tenant-2"))).GetAsync([Arm]);

        Assert.Contains("tenant-1", t1);
        Assert.Contains("tenant-2", t2);
        Assert.Equal(2, calls());
    }

    [Fact]
    public async Task 令牌缓存_登出后只清该账户缓存()
    {
        var runner = CountingRunner(out var calls);
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");
        await (await provider.GetCredentialAsync(Ctx(ProfileA))).GetAsync([Arm]);
        await (await provider.GetCredentialAsync(Ctx(ProfileB))).GetAsync([Arm]);

        await provider.SignOutAsync(new CloudAccount
        {
            AccountId = "azurecli:" + ProfileA,
            Username = "a@outlook.com",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = ProfileA
        });

        await (await provider.GetCredentialAsync(Ctx(ProfileA))).GetAsync([Arm]); // 重新取
        await (await provider.GetCredentialAsync(Ctx(ProfileB))).GetAsync([Arm]); // 仍命中缓存
        Assert.Equal(3, calls());
    }

    [Fact]
    public async Task 令牌缓存_已临近过期的令牌不被复用()
    {
        var count = 0;
        var runner = new FakeRunner
        {
            Responder = _ => new AzureCliResult(0,
                $"{{ \"accessToken\": \"t{Interlocked.Increment(ref count)}\", \"expires_on\": {DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()} }}", "")
        };
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");
        var credential = await provider.GetCredentialAsync(Ctx(ProfileA));

        await credential.GetAsync([Arm]);
        await credential.GetAsync([Arm]);

        Assert.Equal(2, count); // 剩余不足 5 分钟，每次都重新取
    }

    [Fact]
    public async Task 令牌缓存_并发获取只拉起一个CLI进程()
    {
        var runner = CountingRunner(out var calls);
        var provider = new EmbeddedAzureCliIdentityProvider(runner, new FakeProfiles(), "C:\rt\az.cmd");
        var credential = await provider.GetCredentialAsync(Ctx(ProfileA));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => credential.GetAsync([Arm]))));

        Assert.Single(tokens.Distinct());
        Assert.Equal(1, calls());
    }
}
