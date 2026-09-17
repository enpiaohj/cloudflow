using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Scopes;
using CloudFlow.Core.Identity;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Cost;

/// <summary>
/// Azure Cost Management 的 query API 实现。
///
/// 已实测：<c>dimensions</c> 可达，但 <c>query</c> 会被硬限流（连续多次 429）。
/// 因此这里的重点不是"取到数"，而是**限流时如实说明并退避**：
/// 退避后仍拿不到就返回失败原因，由 UI 显示 "—" 与原因 —— 成本卡片显示 0 或旧数字
/// 都会被当成当前账单。
/// </summary>
public sealed class ArmCostService(
    ArmAccessTokenProvider tokenProvider,
    ScopeContext scopeContext,
    ILogger<ArmCostService> logger) : ICostService
{
    private const string ApiVersion = "2024-08-01";

    /// <summary>429 的退避节奏（秒）。Cost Management 的限流窗口以分钟计，退避需要够长才有意义。</summary>
    private static readonly int[] RetryDelaysSeconds = [5, 20, 45];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<(CostSummary? Summary, string? FailureReason)> GetMonthToDateAsync(
        string subscriptionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return (null, "未选择订阅。");
        }

        if (scopeContext.ActiveAccount is null)
        {
            return (null, "尚未登录 Azure 账户。");
        }

        try
        {
            var token = await tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var url =
                $"https://management.azure.com/subscriptions/{subscriptionId}" +
                $"/providers/Microsoft.CostManagement/query?api-version={ApiVersion}";

            var body = """
                {"type":"ActualCost","timeframe":"MonthToDate",
                 "dataset":{"granularity":"None",
                            "aggregation":{"totalCost":{"name":"Cost","function":"Sum"}}}}
                """;

            for (var attempt = 0; ; attempt++)
            {
                var response = await SendAsync(url, token, body, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= RetryDelaysSeconds.Length)
                    {
                        logger.LogWarning("Cost Management 持续限流（429），本次放弃查询");
                        return (null, "Azure Cost Management 正在限流（429），稍后重试。");
                    }

                    var delay = TimeSpan.FromSeconds(RetryDelaysSeconds[attempt]);
                    logger.LogInformation("Cost Management 429，{Delay} 秒后重试", delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // 403 是常见情况：个人账户默认没有 Cost Management 读取权限。
                    // 这不是错误而是权限事实，必须如实说出来，用户才知道去哪授权。
                    var reason = response.StatusCode == HttpStatusCode.Forbidden
                        ? "当前身份没有 Cost Management 读取权限（需要订阅上的 Cost Management Reader）。"
                        : $"成本查询失败（{(int)response.StatusCode}）：{Truncate(content)}";

                    logger.LogWarning("成本查询失败 {Status}: {Body}", (int)response.StatusCode, Truncate(content));
                    return (null, reason);
                }

                return (Parse(content), null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "成本查询异常");
            return (null, $"成本查询失败：{ex.Message}");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        string url, string token, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await Http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>响应是「列定义 + 行数组」，按列名取值，不假设列顺序。</summary>
    private static CostSummary? Parse(string content)
    {
        using var doc = JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("columns", out var columns) ||
            !props.TryGetProperty("rows", out var rows) ||
            rows.GetArrayLength() == 0)
        {
            return null;
        }

        var costIndex = -1;
        var currencyIndex = -1;
        var i = 0;
        foreach (var column in columns.EnumerateArray())
        {
            var name = column.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.Equals(name, "Cost", StringComparison.OrdinalIgnoreCase))
            {
                costIndex = i;
            }
            else if (string.Equals(name, "Currency", StringComparison.OrdinalIgnoreCase))
            {
                currencyIndex = i;
            }
            i++;
        }

        if (costIndex < 0)
        {
            return null;
        }

        // 行是数组不是对象：必须按下标取，且下标要先校验（JsonElement 越界会抛异常）
        var cells = rows[0].EnumerateArray().ToList();
        if (costIndex >= cells.Count)
        {
            return null;
        }

        var costElement = cells[costIndex];
        var amount = costElement.ValueKind == JsonValueKind.Number
            ? costElement.GetDecimal()
            : decimal.TryParse(costElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0m;

        var currency = currencyIndex >= 0 && currencyIndex < cells.Count
            ? cells[currencyIndex].GetString() ?? "USD"
            : "USD";

        return new CostSummary
        {
            Amount = amount,
            Currency = currency,
            PeriodText = $"{DateTimeOffset.Now:yyyy-MM}-01 起至今",
            RetrievedAt = DateTimeOffset.Now
        };
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300];
}
