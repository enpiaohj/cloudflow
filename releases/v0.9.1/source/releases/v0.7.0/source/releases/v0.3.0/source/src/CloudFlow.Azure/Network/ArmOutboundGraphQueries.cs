using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudFlow.Azure.Arm;

namespace CloudFlow.Azure.Network;

/// <summary>
/// 出站解析需要的两条 Resource Graph 查询（设计文档 §20）：
/// 按**私网 IP** 反查 Azure Firewall 与网卡 —— UDR 里只写了下一跳的 IP 地址，
/// 仅凭地址无法知道那台设备是谁，必须反查才知道能把出口解析到哪里。
///
/// 身份取自 <see cref="ArmAccessTokenProvider"/>（读路径，按订阅取 Token），
/// 与 <see cref="ArmSubnetVmCounter"/> 走请求携带身份不同：出站解析是详情页的只读查询，没有 OperationRequest。
///
/// 查询范围限定在当前订阅。不跨订阅不是省事，而是因为私网 IP 在多个 VNet 里可以重复
/// （10.0.0.4 到处都是），放开范围只会换来更多错匹配。
/// </summary>
public sealed class ArmOutboundGraphQueries(ArmAccessTokenProvider tokenProvider)
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private const string FirewallByPrivateIpQuery = """
        Resources
        | where type =~ 'microsoft.network/azurefirewalls'
        | mv-expand ipc = properties.ipConfigurations
        | where tostring(ipc.properties.privateIPAddress) == '{0}'
        | project firewallId = tostring(id), pipId = tostring(ipc.properties.publicIPAddress.id)
        """;

    private const string NicByPrivateIpQuery = """
        Resources
        | where type =~ 'microsoft.network/networkinterfaces'
        | mv-expand ipc = properties.ipConfigurations
        | where tostring(ipc.properties.privateIPAddress) == '{0}'
        | project nicId = tostring(id), vmId = tostring(properties.virtualMachine.id)
        """;

    /// <summary>按私网 IP 找 Azure Firewall。返回全部命中项，由调用方判断是否唯一。</summary>
    public Task<IReadOnlyList<FirewallMatch>> FindFirewallsByPrivateIpAsync(
        string privateIp, string? subscriptionId, CancellationToken ct = default)
        => QueryAsync(FirewallByPrivateIpQuery, privateIp, subscriptionId, row => new FirewallMatch(
            GetString(row, "firewallId"), GetString(row, "pipId")), ct);

    /// <summary>按私网 IP 找网卡（从而找到它属于哪台 VM）。返回全部命中项。</summary>
    public Task<IReadOnlyList<NicMatch>> FindNicsByPrivateIpAsync(
        string privateIp, string? subscriptionId, CancellationToken ct = default)
        => QueryAsync(NicByPrivateIpQuery, privateIp, subscriptionId, row => new NicMatch(
            GetString(row, "nicId"), GetString(row, "vmId")), ct);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string template,
        string privateIp,
        string? subscriptionId,
        Func<JsonElement, T> map,
        CancellationToken ct)
    {
        var token = await tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
        var data = await QueryAsync(token, string.Format(
            template, privateIp.Replace("'", "''", StringComparison.Ordinal)), subscriptionId, ct)
            .ConfigureAwait(false);

        var result = new List<T>();
        foreach (var row in data.EnumerateArray())
        {
            result.Add(map(row));
        }

        return result;
    }

    private async Task<JsonElement> QueryAsync(
        string token, string query, string? subscriptionId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["query"] = query };
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            payload["subscriptions"] = new[] { subscriptionId };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>一条 Azure Firewall 命中：Firewall 资源 ID + 该 IP 配置绑定的公网 IP ID。</summary>
    public sealed record FirewallMatch(string FirewallId, string PublicIpId);

    /// <summary>一条网卡命中：网卡资源 ID + 它挂在哪台 VM 上。</summary>
    public sealed record NicMatch(string NicId, string VmId);
}
