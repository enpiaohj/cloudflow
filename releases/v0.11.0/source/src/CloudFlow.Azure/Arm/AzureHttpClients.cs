namespace CloudFlow.Azure.Arm;

/// <summary>复用连接池，每个请求开始时读取超时配置，不修改已使用的 HttpClient.Timeout。</summary>
public static class AzureHttpClients
{
    private static int _timeoutSeconds = 300;
    public static int TimeoutSeconds
    {
        get => Volatile.Read(ref _timeoutSeconds);
        set => Volatile.Write(ref _timeoutSeconds, value is >= 30 and <= 900 ? value : 300);
    }

    public static HttpClient Create() => new(new AzureRequestTimeoutHandler(
        () => TimeSpan.FromSeconds(TimeoutSeconds)) { InnerHandler = new HttpClientHandler() })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}

public sealed class AzureRequestTimeoutHandler(Func<TimeSpan> timeoutProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var timeout = timeoutProvider();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            // HttpClient 在 Handler 返回之后才缓冲内容；必须在截止时间内包含响应体读取。
            await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TimeoutException(
                $"Azure 请求超过 {timeout.TotalSeconds:0} 秒未完成。请检查网络或代理后刷新重试，也可在「设置 → 网络」调整请求超时。", ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }
}
