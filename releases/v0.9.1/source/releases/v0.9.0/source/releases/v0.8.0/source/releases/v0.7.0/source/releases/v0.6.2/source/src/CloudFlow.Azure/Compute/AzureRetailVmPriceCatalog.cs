using System.Collections.Concurrent;
using System.Text.Json;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 基于 Azure 官方零售价目 API（<c>prices.azure.com</c>）的月度费用预估。
///
/// 这个端点是**公开只读价目表**，不需要 Azure 登录、不访问任何租户/订阅数据——传入的区域和规格
/// 只是查价格用的过滤条件，因此不像 <see cref="ArmRegionCatalog"/>/<see cref="ArmVmSizeCatalog"/>
/// 那样需要 <see cref="Arm.ArmAccessTokenProvider"/>，Demo 模式下一样能查（不违反"Demo 模式不接入
/// 真实 Azure 账户数据"的原意，那条规则针对的是用户自己的资源，不是公开价目表）。
///
/// 一次 $filter 查询通常会命中同一区域/规格下多条价目（Windows / Linux、按需 / Spot 各一条），
/// 用 Windows 关键字与 "Spot" 关键字过滤到刚好一条按需消费价。查询失败或没有命中时返回 null，
/// 调用方（创建向导）直接不显示预估费用，不影响创建流程本身。
/// </summary>
public sealed class AzureRetailVmPriceCatalog : IVmPriceCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = new();

    private readonly ILogger<AzureRetailVmPriceCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AzureRetailVmPriceCatalog(ILogger<AzureRetailVmPriceCatalog> logger)
    {
        _logger = logger;
    }

    public async Task<decimal?> GetMonthlyEstimateUsdAsync(
        string vmSize, string region, bool isWindows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmSize) || string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        var key = $"{region}|{vmSize}|{isWindows}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.MonthlyUsd;
        }

        var estimate = await FetchAsync(vmSize, region, isWindows, ct).ConfigureAwait(false);
        if (estimate is not null)
        {
            _cache[key] = new CacheEntry(estimate, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return estimate;
    }

    private async Task<decimal?> FetchAsync(string vmSize, string region, bool isWindows, CancellationToken ct)
    {
        try
        {
            var filter = $"armRegionName eq '{region}' and armSkuName eq '{vmSize}' " +
                         "and serviceName eq 'Virtual Machines' and priceType eq 'Consumption'";
            var url = "https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=" +
                      Uri.EscapeDataString(filter);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "读取价目失败（{Region}/{Size}）：HTTP {Status}，创建向导将不显示预估费用",
                    region, vmSize, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("productName", out var productNameProp) ||
                    productNameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var productName = productNameProp.GetString() ?? string.Empty;
                if (productName.Contains("Spot", StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains("Low Priority", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isWindowsSku = productName.Contains("Windows", StringComparison.OrdinalIgnoreCase);
                if (isWindowsSku != isWindows)
                {
                    continue;
                }

                if (!item.TryGetProperty("retailPrice", out var priceProp) ||
                    !priceProp.TryGetDecimal(out var hourlyUsd) || hourlyUsd <= 0)
                {
                    continue;
                }

                // 730 小时/月：Azure 定价计算器的官方月度换算惯例（365 天 × 24 小时 / 12 个月）。
                return Math.Round(hourlyUsd * 730m, 2);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取价目异常（{Region}/{Size}），创建向导将不显示预估费用", region, vmSize);
            return null;
        }
    }

    private sealed record CacheEntry(decimal? MonthlyUsd, DateTimeOffset ExpiresAt);
}
