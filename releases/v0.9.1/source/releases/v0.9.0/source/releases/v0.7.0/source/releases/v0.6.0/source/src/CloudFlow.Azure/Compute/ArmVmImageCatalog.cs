using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的镜像目录：两步查市场镜像的 Hyper-V 世代。
///
/// 版本号常是 "latest"（本项目内置镜像清单全用这个），ARM 没有"直接查 latest 详情"的接口，
/// 必须先 <c>GET .../versions?$top=1&amp;$orderby=name desc</c> 解出真实最新版本号，
/// 再拿这个版本号去查 <c>.../versions/{version}</c> 详情里的 <c>hyperVGeneration</c>。
/// 用户自己粘贴具体版本号（非 "latest"）时跳过第一步，直接查该版本。
///
/// 镜像世代是发布后基本不变的事实，缓存 24 小时；失败（比如这个镜像在选定区域不可用）返回
/// null，调用方直接不显示这行提示，不阻塞创建流程——与 <see cref="ArmVmSizeCatalog"/> 同一纪律。
/// </summary>
public sealed class ArmVmImageCatalog : IVmImageCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = new();

    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ILogger<ArmVmImageCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArmVmImageCatalog(ArmAccessTokenProvider tokenProvider, ILogger<ArmVmImageCatalog> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<string?> GetHyperVGenerationAsync(
        string subscriptionId, string region, string imageUrn, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(region) ||
            string.IsNullOrEmpty(imageUrn))
        {
            return null;
        }

        var parts = imageUrn.Split(':');
        if (parts.Length != 4)
        {
            return null;
        }

        var key = $"{subscriptionId}|{region}|{imageUrn}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.HyperVGeneration;
        }

        var generation = await FetchAsync(subscriptionId, region, parts[0], parts[1], parts[2], parts[3], ct)
            .ConfigureAwait(false);
        if (generation is not null)
        {
            _cache[key] = new CacheEntry(generation, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return generation;
    }

    private async Task<string?> FetchAsync(
        string subscriptionId, string region, string publisher, string offer, string sku, string version,
        CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var basePath = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                            $"/providers/Microsoft.Compute/locations/{region}/publishers/{publisher}" +
                            $"/artifacttypes/vmimage/offers/{offer}/skus/{sku}";

            var resolvedVersion = version;
            if (string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
            {
                var listUrl = $"{basePath}/versions?api-version=2023-07-01&$top=1&$orderby=name%20desc";
                var listBody = await GetStringAsync(listUrl, token, ct).ConfigureAwait(false);
                if (listBody is null)
                {
                    return null;
                }

                using var listDoc = JsonDocument.Parse(listBody);
                if (listDoc.RootElement.ValueKind != JsonValueKind.Array ||
                    listDoc.RootElement.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = listDoc.RootElement[0];
                if (!first.TryGetProperty("name", out var nameProp) ||
                    nameProp.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                resolvedVersion = nameProp.GetString();
                if (string.IsNullOrEmpty(resolvedVersion))
                {
                    return null;
                }
            }

            var detailUrl = $"{basePath}/versions/{resolvedVersion}?api-version=2023-07-01";
            var detailBody = await GetStringAsync(detailUrl, token, ct).ConfigureAwait(false);
            if (detailBody is null)
            {
                return null;
            }

            using var detailDoc = JsonDocument.Parse(detailBody);
            if (!detailDoc.RootElement.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty("hyperVGeneration", out var genProp) ||
                genProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return genProp.GetString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取镜像 Hyper-V 世代异常（{Region}/{Publisher}:{Offer}:{Sku}:{Version}）",
                region, publisher, offer, sku, version);
            return null;
        }
    }

    private async Task<string?> GetStringAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private sealed record CacheEntry(string? HyperVGeneration, DateTimeOffset ExpiresAt);
}
