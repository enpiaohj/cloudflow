using CloudFlow.Core.Identity;
using Xunit;

namespace CloudFlow.Core.Tests.Identity;

/// <summary>
/// 统一账户模型回归测试（P0 Spike 步骤 4，规范 §十三）。
/// 账户唯一性由 ProviderType + Provider 原生 ID 决定；UPN / 显示名称不作为主键。
/// </summary>
public sealed class CloudAccountTests
{
    [Fact]
    public void Account_携带Provider类型与ProfileId()
    {
        var entra = new CloudAccount
        {
            AccountId = "entra-object-id",
            Username = "user@company.com",
            DisplayName = "User",
            ProviderType = AuthenticationProviderType.EntraMsal
        };
        var personal = new CloudAccount
        {
            AccountId = "5d3c9f2b7a8e4c1f9b0d6e2a4f8c1b3d",
            Username = "personal@outlook.com",
            DisplayName = "Personal",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "5d3c9f2b7a8e4c1f9b0d6e2a4f8c1b3d"
        };

        Assert.Equal(AuthenticationProviderType.EntraMsal, entra.ProviderType);
        Assert.Null(entra.ProviderProfileId);
        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, personal.ProviderType);
        Assert.Equal("5d3c9f2b7a8e4c1f9b0d6e2a4f8c1b3d", personal.ProviderProfileId);
    }

    [Fact]
    public void 相同UPN不同Provider的账户互为独立身份()
    {
        var entra = new CloudAccount
        {
            AccountId = "entra-1",
            Username = "same@example.com",
            ProviderType = AuthenticationProviderType.EntraMsal
        };
        var cli = new CloudAccount
        {
            AccountId = "cli-1",
            Username = "same@example.com",
            ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
            ProviderProfileId = "abc"
        };

        Assert.NotEqual(entra.AccountId, cli.AccountId);
        Assert.NotEqual(entra.ProviderType, cli.ProviderType);
    }

    [Fact]
    public void CloudCredentialContext_完整绑定账户租户订阅与Provider()
    {
        var context = new CloudCredentialContext
        {
            AccountId = "entra-1",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ProviderType = AuthenticationProviderType.EntraMsal
        };

        Assert.Equal("entra-1", context.AccountId);
        Assert.Equal("tenant-1", context.TenantId);
        Assert.Equal("sub-1", context.SubscriptionId);
        Assert.Equal(AuthenticationProviderType.EntraMsal, context.ProviderType);
        Assert.Null(context.ProviderProfileId);
    }
}
