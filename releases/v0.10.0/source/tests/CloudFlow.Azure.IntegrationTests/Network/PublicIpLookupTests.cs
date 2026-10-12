using System.Net;
using CloudFlow.Modules.Network.Services;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Network;

/// <summary>
/// 「我的当前 IP」的取数逻辑。这一组全部是**假 HTTP 处理器**下的纯单元测试，不联网。
///
/// 为什么值得测这么细：这个返回值会被写进 NSG 规则当来源地址。
/// 一个不该被采信的响应（门户 HTML、错误页、被劫持的正文）如果混进来，
/// 规则会放行一个 Azure 根本不会看到的来源 —— 用户自己连不上，却以为端口已经收紧。
/// 所以"什么算查到了"必须比"看起来像 IP"严格。
/// </summary>
public sealed class PublicIpLookupTests
{
    private static readonly string[] Endpoints = ["https://primary.test", "https://backup.test"];

    /// <summary>回应者拿到的第三个参数是本次请求的取消令牌，用来模拟真实处理器的超时行为。</summary>
    private sealed class StubHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<string> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            lock (Uris)
            {
                Uris.Add(request.RequestUri!.ToString());
            }

            return responder(request, index, ct);
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static HttpPublicIpLookup Build(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder,
        out StubHandler handler,
        TimeSpan? timeout = null,
        TimeSpan? cacheTtl = null,
        Func<DateTimeOffset>? clock = null)
    {
        handler = new StubHandler(responder);
        return new HttpPublicIpLookup(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            Endpoints,
            clock,
            timeout,
            cacheTtl);
    }

    /// <summary>
    /// 每个请求都现造一份响应。HttpResponseMessage 会被消费方（正确地）释放，
    /// 复用同一个实例会让第二次请求读到已释放的正文 —— 真实的 HttpClient 每次都返回新对象。
    /// </summary>
    private static HttpPublicIpLookup Always(HttpResponseMessage template, out StubHandler handler)
    {
        var body = template.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var status = template.StatusCode;
        return Build((_, _, _) => Task.FromResult(Text(body, status)), out handler);
    }

    // ==== 正常路径 ====

    [Fact]
    public async Task 主端点返回纯文本地址_直接采用()
    {
        var lookup = Always(Text("52.141.44.28"), out var handler);

        Assert.Equal("52.141.44.28", await lookup.GetPublicIpAsync());
        Assert.Single(handler.Uris);
        Assert.Equal("https://primary.test/", handler.Uris[0]);
    }

    [Fact]
    public async Task 正文带换行与空白_仍然接受()
    {
        // icanhazip 这类端点会在地址后面带回车换行
        var lookup = Always(Text("  52.141.44.28\r\n"), out _);

        Assert.Equal("52.141.44.28", await lookup.GetPublicIpAsync());
    }

    [Fact]
    public async Task IPv6地址_被接受()
    {
        var lookup = Always(Text("2001:db8::1\n"), out _);

        Assert.Equal("2001:db8::1", await lookup.GetPublicIpAsync());
    }

    // ==== 不该采信的响应 ====

    [Theory]
    [InlineData("<html><body>captive portal</body></html>")]
    [InlineData("error")]
    [InlineData("example.com")]
    [InlineData("1.1.1")]          // 宽松解析会变成 1.1.0.1
    [InlineData("01.02.03.04")]    // 前导零写法不是规范字面量
    [InlineData("12345")]          // 会被当成 32 位整数解析
    [InlineData("fe80::1%eth0")]   // 带作用域 ID，不能写进 NSG 规则
    [InlineData("52.141.44.28 52.141.44.29")] // 两个地址，取哪个都是猜
    public async Task 不是规范的IP字面量_一律丢弃(string body)
    {
        var lookup = Always(Text(body), out _);

        Assert.Null(await lookup.GetPublicIpAsync());
    }

    [Fact]
    public async Task 空正文_返回null()
    {
        var lookup = Always(Text(""), out _);

        Assert.Null(await lookup.GetPublicIpAsync());
    }

    [Fact]
    public async Task 正文超过任何IP字面量的长度_返回null()
    {
        // 顺带说明这里为什么要有长度上限：响应体来自我们控制不了的第三方端点
        var lookup = Always(Text(new string('a', 4096)), out _);

        Assert.Null(await lookup.GetPublicIpAsync());
    }

    [Fact]
    public async Task 非2xx_返回null()
    {
        var lookup = Always(Text("", HttpStatusCode.ServiceUnavailable), out _);

        Assert.Null(await lookup.GetPublicIpAsync());
    }

    // ==== 失败与回落 ====

    [Fact]
    public async Task 主端点不通_回落到备用端点()
    {
        var lookup = Build(
            (_, index, _) => index == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))
                : Task.FromResult(Text("52.141.44.28\n")),
            out var handler);

        Assert.Equal("52.141.44.28", await lookup.GetPublicIpAsync());
        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://backup.test/", handler.Uris[1]);
    }

    [Fact]
    public async Task 主端点返回的正文不可用_也会回落备用端点()
    {
        var lookup = Build(
            (_, index, _) => Task.FromResult(index == 0 ? Text("<html>") : Text("52.141.44.28")),
            out var handler);

        Assert.Equal("52.141.44.28", await lookup.GetPublicIpAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task 两个端点都不可用_返回null而不是编造地址()
    {
        var lookup = Build(
            (_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")),
            out var handler);

        Assert.Null(await lookup.GetPublicIpAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task 端点不响应_超时后返回null()
    {
        var lookup = Build(
            async (_, _, ct) =>
            {
                // 处理器观察取消令牌 —— 真实处理器也是这么表现的
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Text("52.141.44.28");
            },
            out _,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(await lookup.GetPublicIpAsync());
    }

    [Fact]
    public async Task 调用方主动取消_不伪装成查不到而是上抛()
    {
        // 这里超时设得很长，确保触发的是**调用方**的令牌而不是本类自己的 5 秒超时 ——
        // 两者在 catch 里的待遇不同：自己超时是"查不到"，调用方取消必须原样上抛。
        var lookup = Build(
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Text("52.141.44.28");
            },
            out _,
            timeout: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.GetPublicIpAsync(cts.Token));
    }

    // ==== 缓存 ====

    [Fact]
    public async Task 会话内缓存_第二次不再外发()
    {
        var lookup = Always(Text("52.141.44.28"), out var handler);

        await lookup.GetPublicIpAsync();
        await lookup.GetPublicIpAsync();

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task 缓存过期后重新查询()
    {
        var now = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        var lookup = Build(
            (_, _, _) => Task.FromResult(Text("52.141.44.28")),
            out var handler,
            cacheTtl: TimeSpan.FromMinutes(10),
            clock: () => now);

        await lookup.GetPublicIpAsync();
        now = now.AddMinutes(9);
        await lookup.GetPublicIpAsync();
        Assert.Equal(1, handler.Calls);

        now = now.AddMinutes(2);
        await lookup.GetPublicIpAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task 失败不进缓存_端点一恢复就能拿到地址()
    {
        // 把一次瞬时故障缓存十分钟，等于让用户在这十分钟里只能手填来源。
        // 用同一个实例：头两次调用（两个端点各一次）都拿不到，第三次起端点恢复。
        var handler = new StubHandler((_, index, _) => Task.FromResult(
            index < 2 ? Text("<html>") : Text("52.141.44.28")));
        var lookup = new HttpPublicIpLookup(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, Endpoints);

        Assert.Null(await lookup.GetPublicIpAsync());
        Assert.Equal("52.141.44.28", await lookup.GetPublicIpAsync());
        // 恢复后那一次必须真的重新外发，而不是命中一份空的缓存
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task 并发调用_只外发一次()
    {
        var gate = new TaskCompletionSource();
        var lookup = Build(
            async (_, _, _) =>
            {
                await gate.Task;
                return Text("52.141.44.28");
            },
            out var handler);

        var first = lookup.GetPublicIpAsync();
        var second = lookup.GetPublicIpAsync();

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, ip => Assert.Equal("52.141.44.28", ip));
        Assert.Equal(1, handler.Calls);
    }

    // ==== CIDR 推导 ====

    [Fact]
    public void IPv4转成32位掩码()
    {
        Assert.Equal("52.141.44.28/32", PublicIpText.ToCidr("52.141.44.28"));
    }

    [Fact]
    public void IPv6转成128位掩码而不是错误的32()
    {
        // 恒拼 /32 对 IPv6 是一个覆盖 2^96 个地址的网段
        Assert.Equal("2001:db8::1/128", PublicIpText.ToCidr("2001:db8::1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>")]
    public void 拿不到地址时CIDR也是空(string? ip)
    {
        Assert.Null(PublicIpText.ToCidr(ip));
    }
}
