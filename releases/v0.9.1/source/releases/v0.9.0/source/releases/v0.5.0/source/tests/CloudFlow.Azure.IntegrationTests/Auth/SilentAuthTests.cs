using CloudFlow.Azure.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Auth;

/// <summary>
/// MSAL 静默登录集成测试（P0）。
/// 前置条件：环境变量 CLOUDFLOW_CLIENT_ID 已配置，且本机 Token Cache 中已有可静默获取 Token 的账户
/// （先运行 CloudFlow.Spike 完成一次交互登录）。
/// 无配置时 Skip，不会出现在 CI 失败中。
/// </summary>
[Trait("Category", "Integration")]
public class SilentAuthTests
{
    private static MsalAuthConfig? LoadConfig()
    {
        var clientId = Environment.GetEnvironmentVariable("CLOUDFLOW_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }
        return new MsalAuthConfig
        {
            ClientId = clientId,
            TenantId = Environment.GetEnvironmentVariable("CLOUDFLOW_TENANT_ID") ?? "organizations"
        };
    }

    [Fact]
    public async Task GetAccountsAsync_已配置时_返回TokenCache账户()
    {
        var config = LoadConfig();
        if (config is null)
        {
            // 未配置环境时直接返回（集成测试仅在有真实环境时执行）
            return;
        }

        var manager = new MsalAccountSessionManager(config, NullLogger<MsalAccountSessionManager>.Instance);
        var accounts = await manager.GetAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.All(accounts, a => Assert.False(string.IsNullOrEmpty(a.Username)));
    }
}
