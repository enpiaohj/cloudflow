using System.Net.Http.Headers;
using System.Text.Json;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// 订阅发现实现：ARM /subscriptions 一次返回账户可访问的全部订阅，
/// 每条携带 tenantId，可据此识别多 Tenant（设计文档 §72/§73）。
/// </summary>
public sealed class SubscriptionDiscoveryService : ISubscriptionDiscoveryService
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<SubscriptionProfile>> DiscoverAsync(
        AccountSession session, CancellationToken ct = default)
    {
        var token = await session.GetAccessTokenAsync(
            ["https://management.azure.com/.default"], ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://management.azure.com/subscriptions?api-version=2022-12-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudFlowException(CloudFlowErrorCode.AzureError,
                $"订阅发现失败（{(int)response.StatusCode}）：{Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var result = new List<SubscriptionProfile>();
        foreach (var sub in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            result.Add(new SubscriptionProfile
            {
                SubscriptionId = sub.GetProperty("subscriptionId").GetString() ?? "",
                TenantId = sub.TryGetProperty("tenantId", out var t) ? t.GetString() ?? "" : "",
                DisplayName = sub.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                State = sub.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "Enabled",
                IsSelected = true,
                LastRefreshedAt = DateTimeOffset.Now
            });
        }

        return result.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
