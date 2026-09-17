using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的区域目录：<c>GET /subscriptions/{sub}/locations</c>。
///
/// 与 <see cref="ArmVmSizeCatalog"/> 同一套写法（同一个 <see cref="ArmAccessTokenProvider"/>
/// 取令牌、原始 HttpClient 直调、失败降级为空列表、按订阅缓存 6 小时）——区域上下架是低频事件，
/// 不需要每次都打一次 ARM。
/// 只保留 <c>metadata.regionType == "Physical"</c> 的条目：ListLocations 除了真实可创建资源的
/// 区域外还会返回一批"逻辑区域"（用于地理归类，不能拿去创建虚拟机），混进下拉会让用户选中一个
/// 建不出机器的区域。
/// </summary>
public sealed class ArmRegionCatalog : IRegionCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new();

    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ILogger<ArmRegionCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArmRegionCatalog(ArmAccessTokenProvider tokenProvider, ILogger<ArmRegionCatalog> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RegionInfo>> GetRegionsAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return [];
        }

        if (_cache.TryGetValue(subscriptionId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Regions;
        }

        var regions = await FetchAsync(subscriptionId, ct).ConfigureAwait(false);
        if (regions.Count > 0)
        {
            _cache[subscriptionId] = new CacheEntry(regions, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return regions;
    }

    private async Task<IReadOnlyList<RegionInfo>> FetchAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/locations?api-version=2022-12-01";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "读取区域列表失败（订阅 {Subscription}）：HTTP {Status}，创建向导的区域下拉将为空",
                    subscriptionId, (int)response.StatusCode);
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<RegionInfo>();
            foreach (var location in values.EnumerateArray())
            {
                if (!IsPhysicalRegion(location))
                {
                    continue;
                }

                if (!location.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var displayName = location.TryGetProperty("displayName", out var displayProp) &&
                                   displayProp.ValueKind == JsonValueKind.String
                    ? displayProp.GetString() ?? name
                    : name;

                result.Add(new RegionInfo(name, displayName));
            }

            _logger.LogInformation("区域目录：订阅 {Subscription} 载入 {Count} 个区域", subscriptionId, result.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取区域列表异常（订阅 {Subscription}），创建向导的区域下拉将为空", subscriptionId);
            return [];
        }
    }

    /// <summary>
    /// ListLocations 混杂"物理区域"（可创建资源）与"逻辑区域"（仅用于地理归类，不能创建资源）；
    /// <c>metadata.regionType</c> 缺失时保守地当作物理区域，避免把真区域误过滤掉。
    /// </summary>
    private static bool IsPhysicalRegion(JsonElement location)
    {
        if (!location.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!metadata.TryGetProperty("regionType", out var regionTypeProp) ||
            regionTypeProp.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        return string.Equals(regionTypeProp.GetString(), "Physical", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CacheEntry(IReadOnlyList<RegionInfo> Regions, DateTimeOffset ExpiresAt);
}
