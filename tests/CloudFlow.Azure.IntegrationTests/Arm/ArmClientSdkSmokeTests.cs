using CloudFlow.Core.Identity;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Arm;

/// <summary>
/// P0 Spike 步骤 6 端到端冒烟：真实个人 CLI Profile → CallbackTokenCredential
/// → SDK ArmClient → 订阅枚举。Runtime / Profile 不存在时静默跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ArmClientSdkSmokeTests
{
    [Fact]
    public async Task 个人Profile_经SDK_ArmClient枚举真实订阅()
    {
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
            return; // 无已登录个人 Profile：跳过
        }

        var provider = new EmbeddedAzureCliIdentityProvider(runner, profiles, azCmd);
        var account = new CloudAccount
        {
            AccountId = EmbeddedAzureCliIdentityProvider.AccountIdPrefix + profileId,
            Username = "smoke@test",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = profileId
        };

        var subscriptions = await provider.GetSubscriptionsAsync(account);
        var selectedSubscription = subscriptions.FirstOrDefault();
        if (selectedSubscription is null)
        {
            return; // Profile 未关联订阅：跳过
        }

        var factory = new CloudArmClientFactory([provider]);
        var armClient = await factory.CreateAsync(new CloudCredentialContext
        {
            AccountId = account.AccountId,
            TenantId = selectedSubscription.TenantId,
            SubscriptionId = selectedSubscription.SubscriptionId,
            ProviderType = account.ProviderType,
            ProviderProfileId = account.ProviderProfileId
        });

        var listed = new List<string>();
        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync())
        {
            listed.Add(subscription.Id.SubscriptionId ?? "");
        }

        Assert.NotEmpty(listed);
    }
}
