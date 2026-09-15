using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// 把 <see cref="OperationRequest"/> 的认证上下文转成 ARM 凭据上下文。
///
/// 身份三要素 + Provider 信息完全来自请求，禁止任何环境态 / 静态当前账户 ——
/// 这是"排队中的 Job 不会用新账户的凭据"的唯一保证。
/// </summary>
public static class RequestCredential
{
    public static CloudCredentialContext From(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new CloudCredentialContext
        {
            AccountId = request.AccountId,
            TenantId = request.TenantId,
            SubscriptionId = request.SubscriptionId,
            ProviderType = RequireProviderType(request),
            ProviderProfileId = request.ProviderProfileId
        };
    }

    /// <summary>真实 Azure 操作必须有 Provider；缺失说明请求是 Demo 路径误入真实执行器。</summary>
    public static AuthenticationProviderType RequireProviderType(OperationRequest request)
    {
        var providerType = request.ProviderType
            ?? throw new OperationValidationException("缺少必需认证上下文：ProviderType。");

        if (providerType != AuthenticationProviderType.EntraMsal &&
            providerType != AuthenticationProviderType.EmbeddedAzureCli)
        {
            throw new OperationValidationException($"未知的身份 Provider 类型：{providerType}");
        }

        return providerType;
    }
}
