using Azure.ResourceManager;
using CloudFlow.Core.Identity;
using CloudFlow.Azure.Identity;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// Azure Client 工厂平台接口（设计文档 §71；P0 Spike 规范 §十四）。
/// 任何 Module 不得直接 new ArmClient(...)，统一经工厂按 CloudAccount + Tenant 创建，
/// 保证跨 Account / Tenant 操作不混用 Token。
/// </summary>
public interface IAzureClientFactory
{
    /// <summary>按账户的 ProviderType 路由到对应身份 Provider，取 TokenCredential 后创建 ARM Client。</summary>
    Task<ArmClient> CreateAsync(CloudAccount account, string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 统一 ARM Client 工厂：CloudAccount → CloudCredentialContext → ICloudIdentityProvider
/// → TokenCredential → ArmClient（规范 §三/§十四）。不感知具体 Provider 实现。
/// </summary>
public sealed class CloudArmClientFactory : IAzureClientFactory
{
    private readonly IReadOnlyDictionary<AuthenticationProviderType, ICloudIdentityProvider> _providers;

    public CloudArmClientFactory(IEnumerable<ICloudIdentityProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Type);
    }

    public async Task<ArmClient> CreateAsync(
        CloudAccount account,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(account.ProviderType, out var provider))
        {
            throw new NotSupportedException(
                $"账户 {account.AccountId} 的身份 Provider 类型 {account.ProviderType} 未注册。");
        }

        var credential = await provider.GetCredentialAsync(new CloudCredentialContext
        {
            AccountId = account.AccountId,
            TenantId = tenantId,
            // ARM Bearer Token 以租户为作用域，与具体订阅无关；
            // 业务操作仍在各自 ResourceScope/Job 中携带真实 SubscriptionId
            SubscriptionId = "",
            ProviderType = account.ProviderType,
            ProviderProfileId = account.ProviderProfileId
        }, cancellationToken).ConfigureAwait(false);

        return new ArmClient(new CallbackTokenCredential(credential));
    }
}
