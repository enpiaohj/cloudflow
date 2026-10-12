using CloudFlow.Azure.Arm;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Arm;

public sealed class AzureRequestTimeoutTests
{
    private sealed class SlowContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct) =>
            Task.Delay(TimeSpan.FromSeconds(10), ct);
    }

    private sealed class SlowBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new SlowContent() });
    }

    [Fact]
    public async Task 响应头已返回但响应体停滞仍受到超时限制()
    {
        using var http = new HttpClient(new AzureRequestTimeoutHandler(() => TimeSpan.FromMilliseconds(30))
            { InnerHandler = new SlowBodyHandler() });
        await Assert.ThrowsAsync<TimeoutException>(() => http.GetAsync("https://example.invalid"));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task 超时返回中文说明并保留取消原因()
    {
        using var http = new HttpClient(new AzureRequestTimeoutHandler(() => TimeSpan.FromMilliseconds(30))
            { InnerHandler = new SlowHandler() });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => http.GetAsync("https://example.invalid"));
        Assert.Contains("设置 → 网络", error.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task 用户取消不能被报告为超时()
    {
        using var http = new HttpClient(new AzureRequestTimeoutHandler(() => TimeSpan.FromMinutes(5))
            { InnerHandler = new SlowHandler() });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("https://example.invalid", cancel.Token));
    }

    [Fact]
    public async Task 每次请求读取新的超时设置()
    {
        var reads = 0;
        using var http = new HttpClient(new AzureRequestTimeoutHandler(() =>
        {
            reads++;
            return TimeSpan.FromMilliseconds(30);
        }) { InnerHandler = new SlowHandler() });
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<TimeoutException>(() => http.GetAsync("https://example.invalid"));
        Assert.Equal(2, reads);
    }
}
