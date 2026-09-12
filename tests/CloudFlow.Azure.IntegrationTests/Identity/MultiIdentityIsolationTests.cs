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
        var profileId = profiles.GetProfileIds().FirstOrDefault();
        if (profileId is null)
        {
            return; // 无个人 Profile：跳过
        }

        var cliProvider = new EmbeddedAzureCliIdentityProvider(runner, profiles, azCmd);
        var cliAccount = new CloudAccount
        {
            AccountId = EmbeddedAzureCliIdentityProvider.AccountIdPrefix + profileId,
            Username = "isolation@test",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = profileId
        };
        var cliSubs = await cliProvider.GetSubscriptionsAsync(cliAccount);

        // ---- 订阅不串 ----
        Assert.NotEmpty(msalSubs);
        Assert.NotEmpty(cliSubs);
        var msalSubIds = msalSubs.Select(s => s.SubscriptionId).ToHashSet();
        var cliSubIds = cliSubs.Select(s => s.SubscriptionId).ToHashSet();
        Assert.Empty(msalSubIds.Intersect(cliSubIds));

        // ---- ArmClient 不串：同一工厂按账户路由，枚举互不重叠 ----
        var factory = new CloudArmClientFactory(new ICloudIdentityProvider[] { msalProvider, cliProvider });
        var msalClient = await factory.CreateAsync(msalAccount, msalSubs[0].TenantId);
        var cliClient = await factory.CreateAsync(cliAccount, cliSubs[0].TenantId);
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
