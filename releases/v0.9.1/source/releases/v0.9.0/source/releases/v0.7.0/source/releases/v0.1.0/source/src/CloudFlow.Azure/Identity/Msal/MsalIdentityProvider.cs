using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Identity.Msal;

/// <summary>
/// Microsoft Entra 工作/学校账户身份 Provider（P0 Spike 步骤 4，规范 §三/§十五）。
/// 适配现有 MSAL 会话管理器：交互登录、ARM 委托 Token、多订阅发现、单账户登出。
/// 会话失效抛 <see cref="ReauthenticationRequiredException"/>，绝不静默换用其他账户。
/// </summary>
public sealed class MsalIdentityProvider : ICloudIdentityProvider
{
    private readonly IAccountSessionManager _sessions;
    private readonly ISubscriptionDiscoveryService _subscriptionDiscovery;

    public MsalIdentityProvider(
        IAccountSessionManager sessions,
        ISubscriptionDiscoveryService subscriptionDiscovery)
    {
        _sessions = sessions;
        _subscriptionDiscovery = subscriptionDiscovery;
    }

    public AuthenticationProviderType Type => AuthenticationProviderType.EntraMsal;

    public async Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        return await _sessions.AddAccountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
        CloudAccount account,
        CancellationToken cancellationToken = default)
    {
        var session = await ResolveSessionAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
        return await _subscriptionDiscovery.DiscoverAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudAccessToken> GetCredentialAsync(
        CloudCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        var session = await ResolveSessionAsync(context.AccountId, cancellationToken).ConfigureAwait(false);
        return new CloudAccessToken { AcquireAsync = session.GetAccessTokenAsync };
    }

    public async Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        await _sessions.RemoveAccountAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountSession> ResolveSessionAsync(string accountId, CancellationToken cancellationToken)
    {
        return await _sessions.GetSessionAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new ReauthenticationRequiredException(
                "该账户登录会话已失效，请重新登录。");
    }
}
