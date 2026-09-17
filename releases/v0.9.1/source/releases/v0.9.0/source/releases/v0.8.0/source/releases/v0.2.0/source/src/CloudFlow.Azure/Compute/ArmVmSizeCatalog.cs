using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的虚拟机规格目录：<c>GET /subscriptions/{sub}/providers/Microsoft.Compute/skus</c>
/// （带 <c>$filter=location eq '{loc}' and resourceType eq 'virtualMachines'</c>）。
///
/// 为什么从 <c>locations/{loc}/vmSizes</c> 换成 <c>skus</c>：vmSizes 的响应里**没有 capabilities**，
/// 拿不到「每个核心的线程数」（<c>vCPUsPerCore</c>）—— 实测该端点只返回 name / numberOfCores /
/// memoryInMB / maxDataDiskCount / osDiskSizeInMB / resourceDiskSizeInMB。而 skus 带 capabilities，
/// 一次请求即可同时得到 vCPUs / MemoryGB / vCPUsPerCore。
///
/// 换之前担心的"一次返回 7.6 万条"是**不带区域过滤**的情况；带上述 filter 后
/// koreacentral 实测 892 条，与 vmSizes 同量级。
/// （Resource Graph 的 Resources 表不含 compute SKUs，实测返回 0 行，所以必须走 ARM。）
///
/// 规格值与区域有关（可用规格按区域上下架），故按 订阅+区域 缓存结果。
/// 规格上下架是低频事件，缓存 6 小时；失败不缓存，下次刷新会重试。
/// </summary>
public sealed class ArmVmSizeCatalog : IVmSizeCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new();

    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ILogger<ArmVmSizeCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArmVmSizeCatalog(ArmAccessTokenProvider tokenProvider, ILogger<ArmVmSizeCatalog> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, VmSizeInfo>> GetBySizeAsync(
        string subscriptionId,
        string location,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(location))
        {
            return Empty;
        }

        var key = $"{subscriptionId}|{location}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Sizes;
        }

        var sizes = await FetchAsync(subscriptionId, location, ct).ConfigureAwait(false);
        if (sizes.Count > 0)
        {
            _cache[key] = new CacheEntry(sizes, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return sizes;
    }

    private async Task<IReadOnlyDictionary<string, VmSizeInfo>> FetchAsync(
        string subscriptionId,
        string location,
        CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                      $"/providers/Microsoft.Compute/skus?api-version=2021-07-01" +
                      $"&$filter=location eq '{location}' and resourceType eq 'virtualMachines'";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 降级路径：内存列显示"—"，虚拟机列表本身照常可用
                _logger.LogWarning(
                    "读取虚拟机规格失败（订阅 {Subscription} / {Location}）：HTTP {Status}，内存与 vCPU 列将显示 —",
                    subscriptionId, location, (int)response.StatusCode);
                return Empty;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("value", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            var result = new Dictionary<string, VmSizeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var size in values.EnumerateArray())
            {
                if (!size.TryGetProperty("name", out var nameProp) ||
                    nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // skus 把规格值放在 capabilities 数组里（[{name,value}]），不是同级属性
                var capabilities = ReadCapabilities(size);
                if (!capabilities.TryGetValue("MemoryGB", out var memoryGb) ||
                    !int.TryParse(memoryGb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gb) ||
                    !capabilities.TryGetValue("vCPUs", out var vcpus) ||
                    !int.TryParse(vcpus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cores))
                {
                    continue;
                }

                // vCPUsPerCore 是可选能力：缺失时留 null，由界面显示 "—"，不猜 1 或 2
                int? perCore = capabilities.TryGetValue("vCPUsPerCore", out var perCoreText) &&
                               int.TryParse(perCoreText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

                result[name] = new VmSizeInfo(gb * 1024, cores, perCore);
            }

            _logger.LogInformation(
                "虚拟机规格目录：订阅 {Subscription} / {Location} 载入 {Count} 个规格",
                subscriptionId, location, result.Count);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "读取虚拟机规格异常（订阅 {Subscription} / {Location}），内存与 vCPU 列将显示 —",
                subscriptionId, location);
            return Empty;
        }
    }

    /// <summary>
    /// 把 SKU 的 <c>capabilities</c> 数组摊平成 name → value 字典。
    /// 值一律按字符串读：该端点的 value 是字符串（"4"、"True"），不要假设数字类型。
    /// </summary>
    private static Dictionary<string, string> ReadCapabilities(JsonElement size)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!size.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var capability in capabilities.EnumerateArray())
        {
            if (!capability.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String ||
                !capability.TryGetProperty("value", out var valueProp))
            {
                continue;
            }

            var capabilityName = nameProp.GetString();
            var capabilityValue = valueProp.ValueKind switch
            {
                JsonValueKind.String => valueProp.GetString(),
                JsonValueKind.Number => valueProp.ToString(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => null
            };

            if (!string.IsNullOrEmpty(capabilityName) && capabilityValue is not null)
            {
                result[capabilityName] = capabilityValue;
            }
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, VmSizeInfo> Empty =
        new Dictionary<string, VmSizeInfo>(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(IReadOnlyDictionary<string, VmSizeInfo> Sizes, DateTimeOffset ExpiresAt);
}
