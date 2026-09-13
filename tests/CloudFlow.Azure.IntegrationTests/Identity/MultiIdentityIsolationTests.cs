using CloudFlow.Core.Identity;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Auth;
using CloudFlow.Azure.Identity.Msal;
using CloudFlow.Azure.Identity;
using CloudFlow.Azure.Identity.AzureCli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity;

/// <summary>
/// P0 Spike 步骤 7：多身份切换隔离（规范 §十二）。
/// 用真实双身份验证：MSAL 企业账户与嵌入式 CLI 个人账户的
/// 订阅集合互不重叠，SDK ArmClient 各自枚举各自的可访问宇宙。
/// 任一身份不可用时静默跳过（与 SilentAuthTests 同一约定）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MultiIdentityIsolationTests
{
    [Fact]
    public async Task Entra与企业CLI双身份_订阅与ArmClient互不串用()
    {
        // ---- Entra MSAL 身份 ----
        var msalSessions = new MsalAccountSessionManager(
            new MsalAuthConfig(), NullLogger<MsalAccountSessionManager>.Instance);
        var session = await msalSessions.TryRestoreSessionAsync();
        if (session is null)
        {
            return; // 无企业登录缓存：跳过
        }

        var msalProvider = new MsalIdentityProvider(
            msalSessions, new CloudFlow.Azure.Arm.SubscriptionDiscoveryService());
        var msalAccount = session.Account;
        var msalSubs = await msalProvider.GetSubscriptionsAsync(msalAccount);

        // ---- Embedded CLI 个人身份 ----
        var runner = new AzureCliProcessRunner();
        var runtime = new AzureCliRuntimeManager(runner);
        string azCmd;
        try
        {
            azCmd = runtime.ResolveAzCmd();
        }
        catch (AzureCliException)
        {
            return; // Runtime 未就绪：跳过
        }

        var profiles = new AzureCliProfileManager(runner, azCmd);
        var cliProvider = new EmbeddedAzureCliIdentityProvider(runner, profiles, azCmd);

        // 机器上可能存在多个 Profile（含登录未完成留下的空 Profile），
        // 取第一个能发现到订阅的已登录 Profile；都不行则跳过。
        CloudAccount? cliAccount = null;
        IReadOnlyList<SubscriptionProfile> cliSubs = [];
        foreach (var profileId in profiles.GetProfileIds())
        {
            var candidate = new CloudAccount
            {
                AccountId = EmbeddedAzureCliIdentityProvider.AccountIdPrefix + profileId,
                Username = "isolation@test",
                ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
                ProviderProfileId = profileId
            };

            IReadOnlyList<SubscriptionProfile> discovered;
            try
            {
                discovered = await cliProvider.GetSubscriptionsAsync(candidate);
            }
            catch (AzureCliException)
            {
                continue; // 该 Profile 未登录或不可用
            }

            if (discovered.Count > 0)
            {
                cliAccount = candidate;
                cliSubs = discovered;
                break;
            }
        }

        if (cliAccount is null)
        {
            return; // 无已登录个人 Profile：跳过
        }

        // ---- 订阅不串 ----
        Assert.NotEmpty(msalSubs);
        Assert.NotEmpty(cliSubs);
        var msalSubIds = msalSubs.Select(s => s.SubscriptionId).ToHashSet();
        var cliSubIds = cliSubs.Select(s => s.SubscriptionId).ToHashSet();
        Assert.Empty(msalSubIds.Intersect(cliSubIds));

        // ---- ArmClient 不串：同一工厂按账户路由，枚举互不重叠 ----
        var factory = new CloudArmClientFactory(new ICloudIdentityProvider[] { msalProvider, cliProvider });
        var msalClient = await factory.CreateAsync(Context(msalAccount, msalSubs[0]));
        var cliClient = await factory.CreateAsync(Context(cliAccount, cliSubs[0]));
        Assert.NotSame(msalClient, cliClient);

        var msalArmIds = await ListSubscriptionIdsAsync(msalClient);
        var cliArmIds = await ListSubscriptionIdsAsync(cliClient);
        Assert.NotEmpty(msalArmIds);
        Assert.NotEmpty(cliArmIds);
        Assert.Empty(msalArmIds.Intersect(cliArmIds));

        // 各自 ArmClient 至少能看到订阅发现给出的同一订阅（Provider 内自洽）
        Assert.Subset(msalArmIds, msalSubIds);
        Assert.Subset(cliArmIds, cliSubIds);
    }

    private static CloudCredentialContext Context(CloudAccount account, SubscriptionProfile subscription) => new()
    {
        AccountId = account.AccountId,
        TenantId = subscription.TenantId,
        SubscriptionId = subscription.SubscriptionId,
        ProviderType = account.ProviderType,
        ProviderProfileId = account.ProviderProfileId
    };

    private static async Task<HashSet<string>> ListSubscriptionIdsAsync(global::Azure.ResourceManager.ArmClient client)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var subscription in client.GetSubscriptions().GetAllAsync())
        {
            if (subscription.Id.SubscriptionId is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
