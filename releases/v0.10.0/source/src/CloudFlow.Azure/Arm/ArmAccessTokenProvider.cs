using CloudFlow.Azure.Identity;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Scopes;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// 统一的 ARM 访问令牌获取入口。
/// 企业（MSAL）与个人（嵌入式 Azure CLI）账户走同一条路径，不假设存在 MSAL 会话
/// —— 个人账户本就没有 MSAL 会话，早期实现据此判断会误报“登录会话已失效”。
///
/// 用于 SDK 未覆盖的 ARM REST 调用（Resource Graph、Azure Monitor Metrics）。
/// </summary>
public sealed class ArmAccessTokenProvider
{
    private const string ArmScope = "https://management.azure.com/.default";

    private readonly CloudAccountDirectory _directory;
    private readonly ScopeContext _scopeContext;

    public ArmAccessTokenProvider(CloudAccountDirectory directory, ScopeContext scopeContext)
    {
        _directory = directory;
        _scopeContext = scopeContext;
    }

    /// <summary>取 management.azure.com 的访问令牌。未选择账户时抛 <see cref="NotConfiguredException"/>。</summary>
    public async Task<string> GetAsync(string? subscriptionId = null, CancellationToken ct = default)
    {
        var account = _scopeContext.ActiveAccount
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");

        return await GetAsync(ActiveAccountContext.Create(account, _scopeContext, subscriptionId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 按显式给出的认证上下文取令牌。
    /// 写操作执行器走这条重载：身份完全来自 OperationRequest，不读 ScopeContext ——
    /// 否则账户切换后，排队中的 Job 会拿新账户的令牌去打旧订阅。
    /// </summary>
    public async Task<string> GetAsync(Core.Identity.CloudCredentialContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var provider = _directory.FindProvider(context.ProviderType)
            ?? throw new NotConfiguredException($"未注册的身份 Provider：{context.ProviderType}");

        var credential = await provider.GetCredentialAsync(context, ct).ConfigureAwait(false);

        return await credential.AcquireAsync([ArmScope], ct).ConfigureAwait(false);
    }
}
