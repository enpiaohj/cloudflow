using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Network;

/// <summary>
/// 用 Azure Resource Graph 统计子网内的虚拟机数量（设计文档 §25 共享 NSG 影响分析）。
///
/// 走 Resource Graph 而不是逐台读 ARM：一次查询就能拿到跨订阅的结果，
/// 而影响分析对话框必须立刻显示，不能等 N 次往返。
///
/// 身份来自请求携带的认证上下文（<see cref="RequestCredential"/>），不读 ScopeContext。
/// 查询失败一律返回 null，由调用方降级为"只显示 NSG 名"——不能用 0 冒充"没有别的机器"。
/// </summary>
public sealed class ArmSubnetVmCounter(
    ArmAccessTokenProvider tokenProvider,
    ILogger<ArmSubnetVmCounter> logger) : ISubnetVmCounter
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// NIC 的 properties.virtualMachine.id 直接指向 VM，不必再 join VM 表 ——
    /// 一台 VM 可能挂多块 NIC，因此用 dcount 去重。
    /// </summary>
    private const string CountQueryTemplate = """
        Resources
        | where type =~ 'microsoft.network/networkinterfaces'
        | mv-expand ipc = properties.ipConfigurations
        | extend subnetId = tolower(tostring(ipc.properties.subnet.id))
        | extend vmId = tolower(tostring(properties.virtualMachine.id))
        | where subnetId == '{0}'
        | summarize vmCount = dcount(vmId)
        """;

    public async Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subnetId))
        {
            return null;
        }

        try
        {
            var token = await tokenProvider.GetAsync(RequestCredential.From(request), ct).ConfigureAwait(false);
            var data = await QueryAsync(token, string.Format(
                CountQueryTemplate, subnetId.ToLowerInvariant().Replace("'", "''", StringComparison.Ordinal)),
                ct).ConfigureAwait(false);

            foreach (var row in data.EnumerateArray())
            {
                if (row.TryGetProperty("vmCount", out var count) &&
                    (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ||
                     count.ValueKind == JsonValueKind.String && int.TryParse(count.GetString(), out value)))
                {
                    return value;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "子网 {SubnetId} 的 VM 数量查询失败，影响分析降级为只显示 NSG 名", subnetId);
            return null;
        }
    }

    private static async Task<JsonElement> QueryAsync(string token, string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = query });

        using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("data").Clone();
    }
}
