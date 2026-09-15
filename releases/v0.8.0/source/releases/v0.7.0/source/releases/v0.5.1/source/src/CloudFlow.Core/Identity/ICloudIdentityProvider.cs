namespace CloudFlow.Core.Identity;

/// <summary>
/// 统一身份 Provider 契约（P0 Spike，规范 §三）。
/// 上层模块只面向本接口与 CloudCredentialContext，禁止判断账户类型
/// 或直接操作 MSAL / Azure CLI / ArmClient。
/// 链路：CloudAccount → CloudCredentialContext → ICloudIdentityProvider
///       → CloudAccessToken → IAzureClientFactory → ArmClient。
/// </summary>
public interface ICloudIdentityProvider
{
    AuthenticationProviderType Type { get; }

    /// <summary>发起交互式登录（系统浏览器 / Microsoft 官方页面），返回新登录的账户。</summary>
    Task<CloudAccount> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>发现指定账户可访问的订阅。</summary>
    Task<IReadOnlyList<SubscriptionProfile>> GetSubscriptionsAsync(
        CloudAccount account,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 <see cref="CloudCredentialContext"/> 获取访问令牌能力。
    /// 上下文不完整或会话失效时抛出明确错误，绝不静默换用其他账户。
    /// </summary>
    Task<CloudAccessToken> GetCredentialAsync(
        CloudCredentialContext context,
        CancellationToken cancellationToken = default);

    /// <summary>登出并清除该账户的 CloudFlow 本机凭据状态，不影响其他账户。</summary>
    Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken = default);
}
