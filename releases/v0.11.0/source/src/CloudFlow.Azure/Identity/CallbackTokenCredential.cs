using Azure.Core;
using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Identity;

/// <summary>
/// 把 <see cref="CloudAccessToken"/>（内存回调）桥接为 Azure.SDK TokenCredential（P0 Spike 步骤 6）。
/// Token 永不落盘、不写日志；有效期向 SDK 声明为保守短窗口，SDK 据此缓存并在到期前经回调续取。
/// </summary>
public sealed class CallbackTokenCredential(CloudAccessToken tokenSource) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var token = await tokenSource
            .GetAsync(requestContext.Scopes, cancellationToken)
            .ConfigureAwait(false);

        // CLI/MSAL 令牌实际寿命约 60 分钟；此处声明保守短有效期，保证过期刷新路径始终经过本回调
        return new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
