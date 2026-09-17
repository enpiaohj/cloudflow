using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的资源组目录：<c>GET /subscriptions/{sub}/resourcegroups</c>，翻页取全部（不止第一页）。
///
/// 与 <see cref="ArmRegionCatalog"/>/<see cref="ArmVmSizeCatalog"/> 同一套写法（同一个
/// <see cref="ArmAccessTokenProvider"/> 取令牌、原始 HttpClient 直调、失败降级为空列表、
/// 按订阅缓存）——资源组的创建/删除是低频事件，缓存 10 分钟；比区域/规格短，因为用户
/// 很可能刚在这个向导里新建了一个资源组，下次打开就想立刻看到它，不想等 6 小时。
/// </summary>
public sealed class ArmResourceGroupCatalog : IResourceGroupCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = new();

    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ILogger<ArmResourceGroupCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArmResourceGroupCatalog(ArmAccessTokenProvider tokenProvider, ILogger<ArmResourceGroupCatalog> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ResourceGroupInfo>> GetAllAsync(
        string subscriptionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return [];
        }

        if (_cache.TryGetValue(subscriptionId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Groups;
        }

        var groups = await FetchAllAsync(subscriptionId, ct).ConfigureAwait(false);
        if (groups.Count > 0)
        {
            _cache[subscriptionId] = new CacheEntry(groups, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return groups;
    }

    private async Task<IReadOnlyList<ResourceGroupInfo>> FetchAllAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                      "/resourcegroups?api-version=2021-04-01";

            var result = new List<ResourceGroupInfo>();

            // 一个订阅下的资源组可能超过一页（默认约 100 条/页），必须跟着 nextLink 翻完，
            // 否则"资源组比较多的订阅"永远只能看到前一部分——这正是用户反馈的问题本身。
            while (url is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "读取资源组列表失败（订阅 {Subscription}）：HTTP {Status}，创建向导的资源组下拉将退回按已知虚拟机反推",
                        subscriptionId, (int)response.StatusCode);
                    return [];
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("value", out var values) &&
                    values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rg in values.EnumerateArray())
                    {
                        if (rg.TryGetProperty("name", out var nameProp) &&
                            nameProp.ValueKind == JsonValueKind.String &&
                            nameProp.GetString() is { Length: > 0 } name)
                        {
                            var location = rg.TryGetProperty("location", out var locationProp) &&
                                           locationProp.ValueKind == JsonValueKind.String
                                ? locationProp.GetString() ?? ""
                                : "";
                            result.Add(new ResourceGroupInfo(name, location));
                        }
                    }
                }

                url = doc.RootElement.TryGetProperty("nextLink", out var nextLinkProp) &&
                      nextLinkProp.ValueKind == JsonValueKind.String
                    ? nextLinkProp.GetString()
                    : null;
            }

            _logger.LogInformation("资源组目录：订阅 {Subscription} 载入 {Count} 个资源组", subscriptionId, result.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "读取资源组列表异常（订阅 {Subscription}），创建向导的资源组下拉将退回按已知虚拟机反推",
                subscriptionId);
            return [];
        }
    }

    private sealed record CacheEntry(IReadOnlyList<ResourceGroupInfo> Groups, DateTimeOffset ExpiresAt);
}
