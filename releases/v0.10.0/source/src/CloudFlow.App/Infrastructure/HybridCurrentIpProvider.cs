using CloudFlow.Core.Scopes;
using CloudFlow.Data.Stores;
using CloudFlow.Modules.Network.Services;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 「我的当前 IP」的 Demo / 真实分流。
///
/// Demo（未登录）用不可路由的文档地址，界面会明确标注是演示值。
/// 登录真实账户后走 <see cref="IPublicIpLookup"/> 查**本机出口**公网 IP ——
/// 那个地址才是 Azure 侧看到的来源，写进 NSG 规则才有意义。
///
/// 查不到时一律返回 null，由对话框要求手填。**绝不用演示地址顶替**：
/// 用户会以为"只放行我自己"，实际放行的是别人，结果是自己连不上、还误以为端口是安全的。
/// </summary>
public sealed class HybridCurrentIpProvider(
    MockCurrentIpProvider demo,
    IPublicIpLookup lookup,
    AppSettingsStore settings,
    ScopeContext scopeContext) : ICurrentIpProvider
{
    public async Task<string?> GetCurrentIpAsync(CancellationToken ct = default)
    {
        if (scopeContext.ActiveAccount is null)
        {
            return await demo.GetCurrentIpAsync(ct).ConfigureAwait(false);
        }

        // 设置里关掉自动查询后，这里一次外发请求都不会发出。
        // 这是本应用唯一会主动往外发的请求，用户有权关掉它。
        if (!settings.Current.AutoDetectPublicIp)
        {
            return null;
        }

        return await lookup.GetPublicIpAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// CIDR 形式。**从同一个 IP 推导，而不是再查一次** ——
    /// 两次独立查询不仅多打一次第三方端点，还可能在两次之间拿到不同的地址，
    /// 导致对话框里 IP 与 CIDR 对不上。
    /// </summary>
    public async Task<string?> GetCurrentIpCidrAsync(CancellationToken ct = default) =>
        PublicIpText.ToCidr(await GetCurrentIpAsync(ct).ConfigureAwait(false));
}
