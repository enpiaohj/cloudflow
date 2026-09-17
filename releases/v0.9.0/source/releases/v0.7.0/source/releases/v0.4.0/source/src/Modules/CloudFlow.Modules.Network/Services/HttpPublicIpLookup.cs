using System.Net.Http;
using System.Text;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 用 HTTPS 回显端点查询出口公网 IP（设计文档 §24「My Current IP」的数据来源）。
///
/// **为什么是 HTTPS 回显而不是 DNS 回显**：本机 HTTPS 全部经由系统代理出口，
/// 而 DNS 查询不走代理、直连解析器 —— 两条路看到的是**不同的源地址**。
/// 实测中 DNS 那套返回了本机 ISP 地址，而 Azure 侧 Activity Log 记录的真实调用来源
/// 是代理出口地址，两者不一致。用 DNS 的结果建 NSG 规则，规则会放行一个
/// Azure 根本不会看到的来源，用户自己反而连不上，却以为端口已经收紧。
///
/// **为什么不用 Activity Log 当运行时数据源**：它虽然权威（是 Azure 自己记的），
/// 但只记录写操作、有摄入延迟、还需要额外的 `Microsoft.Insights/eventtypes/management/read` 权限 ——
/// 用户第一次打开对话框时很可能拿不到。它在本项目里的角色是**验证手段**，不是运行时依赖。
///
/// 这正是应用**唯一**的外发请求：关闭设置里的「自动查询我的公网 IP」后一次都不会发出。
/// </summary>
public sealed class HttpPublicIpLookup : IPublicIpLookup
{
    /// <summary>主端点（ipify，纯文本）与备用端点（icanhazip，纯文本，正文带换行）。</summary>
    public static readonly IReadOnlyList<string> DefaultEndpoints =
        ["https://api.ipify.org", "https://icanhazip.com"];

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>会话内缓存时长。避免每打开一次对话框就打一次第三方端点。</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 正文长度上限。IP 字面量最长 45 字符（IPv6 全展开），留出余量取 64。
    /// 超过这个长度说明对面返回的不是回显地址（HTML、错误页之类），直接丢弃 ——
    /// 顺带避免被一个超大响应体拖住。
    /// </summary>
    private const int MaxBodyBytes = 64;

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheTtl;

    /// <summary>串行化查询：并发打开对话框时不重复外发同一个请求。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedIp;
    private DateTimeOffset _cachedAt;

    /// <param name="http">调用方持有生命周期，本类不负责释放。</param>
    /// <param name="clock">可注入的时钟，供测试驱动缓存过期；默认使用系统时钟。</param>
    public HttpPublicIpLookup(
        HttpClient http,
        IReadOnlyList<string>? endpoints = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null,
        TimeSpan? cacheTtl = null)
    {
        _http = http;
        _endpoints = endpoints ?? DefaultEndpoints;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _timeout = timeout ?? DefaultTimeout;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock();
            if (_cachedIp is not null && now - _cachedAt < _cacheTtl)
            {
                return _cachedIp;
            }

            foreach (var endpoint in _endpoints)
            {
                var ip = await TryEndpointAsync(endpoint, ct).ConfigureAwait(false);
                if (ip is not null)
                {
                    _cachedIp = ip;
                    _cachedAt = now;
                    return ip;
                }
            }

            // 失败**不进缓存**。把一次瞬时故障缓存十分钟，等于让用户在这十分钟里
            // 只能手填来源 —— 而故障本身可能只持续了一秒。
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TryEndpointAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(_timeout);

            using var response = await _http.GetAsync(endpoint, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);

            var buffer = new byte[MaxBodyBytes];
            var read = await stream.ReadAtLeastAsync(buffer, MaxBodyBytes, throwOnEndOfStream: false, attempt.Token)
                .ConfigureAwait(false);
            if (read == 0 || read == MaxBodyBytes)
            {
                // 空响应不是地址；把 64 字节填满说明正文比任何 IP 字面量都长
                return null;
            }

            // 端点返回的是 ASCII 正文；非 ASCII 字节会被替换字符破坏，随后在校验处被丢掉
            return PublicIpText.ParseLiteral(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方自己取消的，如实上抛，不要伪装成"查不到"
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // 超时/连不上/响应读到一半断了 —— 一律当作"查不到"，由调用方退回手填
            return null;
        }
    }
}
