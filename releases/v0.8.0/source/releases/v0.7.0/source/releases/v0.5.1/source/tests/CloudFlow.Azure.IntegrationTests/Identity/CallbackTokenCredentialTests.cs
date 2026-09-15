using Azure.Core;
using CloudFlow.Core.Identity;
using CloudFlow.Azure.Identity;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity;

/// <summary>
/// CallbackTokenCredential 契约测试（P0 Spike 步骤 6）：
/// 把 CloudAccessToken 内存回调桥接为 Azure.SDK TokenCredential。
/// </summary>
public sealed class CallbackTokenCredentialTests
{
    [Fact]
    public async Task GetToken_透传Scope并返回回调令牌()
    {
        string? capturedScope = null;
        var source = new CloudAccessToken
        {
            AcquireAsync = (scopes, ct) =>
            {
                capturedScope = scopes.First();
                return Task.FromResult("callback-token-value");
            }
        };
        var credential = new CallbackTokenCredential(source);

        var token = credential.GetToken(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            CancellationToken.None);

        Assert.Equal("https://management.azure.com/.default", capturedScope);
        Assert.Equal("callback-token-value", token.Token);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(4), "应提供令 SDK 缓存的有效期");
    }
}
