using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.ResourceGraph;

/// <summary>
/// 基于 Azure Resource Graph 的 VM Inventory（设计文档 §17）。
/// 原则：Resource Graph = Discovery / Inventory / Search；ARM API = 详细信息 / Write Operations。
/// 一次查询跨多个 Subscription 聚合，避免逐 VM GET。
///
/// 登录后由 HybridVmInventoryService 启用；未登录时 UI 使用 Mock 数据。
/// </summary>
public sealed class ResourceGraphVmInventoryService : IVmInventoryService
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    /// <summary>完整查询：VM 基本字段 + Public IP 关联（NIC → PublicIPAddress join）。</summary>
    private const string FullQuery = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend osType = tostring(properties.storageProfile.osDisk.osType),
                 vmSize = tostring(properties.hardwareProfile.vmSize),
                 powerState = tostring(properties.extended.instanceView.powerState),
                 computerName = tostring(properties.osProfile.computerName),
                 privateIp = tostring(properties.extended.instanceView.privateIps)
        | join kind=leftouter (
            Resources
            | where type =~ 'microsoft.network/networkinterfaces'
            | where isnotempty(properties.virtualMachine)
            | extend vmId = tostring(properties.virtualMachine.id)
            | mv-expand ipconfig = properties.ipConfigurations
            | extend pipId = tostring(ipconfig.properties.publicIPAddress.id)
            | where isnotempty(pipId)
            | project vmId, pipId
        ) on $left.resourceId == $right.vmId
        | join kind=leftouter (
            Resources
            | where type =~ 'microsoft.network/publicipaddresses'
            | project pipId = tostring(id), publicIp = tostring(properties.ipAddress)
        ) on pipId
        | project name, resourceId = tostring(id), location, resourceGroup, subscriptionId,
                  osType, vmSize, powerState, computerName, privateIp,
                  publicIp = tostring(publicIp)
        """;

    /// <summary>回退查询：仅 VM 基本字段（join 失败时仍保证 Inventory 可用）。</summary>
    private const string SimpleQuery = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend osType = tostring(properties.storageProfile.osDisk.osType),
                 vmSize = tostring(properties.hardwareProfile.vmSize),
                 powerState = tostring(properties.extended.instanceView.powerState),
                 computerName = tostring(properties.osProfile.computerName),
                 privateIp = tostring(properties.extended.instanceView.privateIps)
        | project name, resourceId = tostring(id), location, resourceGroup, subscriptionId,
                  osType, vmSize, powerState, computerName, privateIp
        """;

    private readonly IAccountSessionManager _sessions;
    private readonly ScopeContext _scopeContext;
    private readonly ILogger<ResourceGraphVmInventoryService> _logger;
    private static readonly HttpClient Http = new();

    public ResourceGraphVmInventoryService(
        IAccountSessionManager sessions,
        ScopeContext scopeContext,
        ILogger<ResourceGraphVmInventoryService> logger)
    {
        _sessions = sessions;
        _scopeContext = scopeContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default)
    {
        var accountId = _scopeContext.ActiveAccount?.AccountId
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");
        var session = await _sessions.GetSessionAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ReauthenticationRequiredException("登录会话已失效，请重新登录。");

        var token = await session.GetAccessTokenAsync(
            ["https://management.azure.com/.default"], ct).ConfigureAwait(false);

        // Scope → 订阅过滤：AllAccessible / Tenant / AllAccounts 省略 subscriptions
        // （ARG 默认查询 Token 可访问的全部订阅）；Single / Multiple 用显式列表
        IReadOnlyList<string>? subscriptions = scope.Mode switch
        {
            ScopeMode.SingleSubscription or ScopeMode.MultipleSubscriptions => scope.SubscriptionIds,
            _ => null
        };

        JsonElement data;
        try
        {
            data = await QueryResourceGraphAsync(token, subscriptions, FullQuery, ct).ConfigureAwait(false);
        }
        catch (CloudFlowException ex)
        {
            // 完整查询（含 IP join）失败时回退简单查询，保证 Inventory 可用
            _logger.LogWarning(ex, "ARG 完整查询失败，回退简单查询");
            data = await QueryResourceGraphAsync(token, subscriptions, SimpleQuery, ct).ConfigureAwait(false);
        }

        return MapRows(data);
    }

    private async Task<JsonElement> QueryResourceGraphAsync(
        string token, IReadOnlyList<string>? subscriptions, string query, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["query"] = query };
        if (subscriptions is { Count: > 0 })
        {
            payload["subscriptions"] = subscriptions;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudFlowException(CloudFlowErrorCode.AzureError,
                $"Resource Graph 查询失败（{(int)response.StatusCode}）：{Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private IReadOnlyList<VmSummary> MapRows(JsonElement data)
    {
        var subNames = _scopeContext.AvailableSubscriptions
            .GroupBy(s => s.SubscriptionId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        var result = new List<VmSummary>();
        foreach (var row in data.EnumerateArray())
        {
            var subscriptionId = GetString(row, "subscriptionId");
            var powerState = MapPowerState(GetString(row, "powerState"));
            var privateIp = GetString(row, "privateIp");
            var computerName = GetString(row, "computerName");
            var osType = GetString(row, "osType");

            result.Add(new VmSummary
            {
                ResourceId = GetString(row, "resourceId"),
                Name = GetString(row, "name"),
                ComputerName = string.IsNullOrEmpty(computerName)
                    ? GetString(row, "name").ToLowerInvariant()
                    : computerName,
                SubscriptionId = subscriptionId,
                SubscriptionName = subNames.TryGetValue(subscriptionId ?? "", out var n)
                    ? n
                    : ShortId(subscriptionId),
                ResourceGroupName = GetString(row, "resourceGroup"),
                Region = GetString(row, "location"),
                VmSize = GetString(row, "vmSize"),
                OsType = string.Equals(osType, "Linux", StringComparison.OrdinalIgnoreCase)
                    ? VmOsType.Linux
                    : VmOsType.Windows,
                PowerState = powerState,
                PublicIp = string.IsNullOrEmpty(GetString(row, "publicIp")) ? null : GetString(row, "publicIp"),
                PrivateIp = string.IsNullOrEmpty(privateIp) ? null : privateIp.Split(',')[0].Trim(),
                CpuPercent = null // CPU 需 Metrics API，P1 列表不显示（显示"—"）
            });
        }

        _logger.LogInformation("Resource Graph inventory: {Count} VMs", result.Count);
        return [.. result.OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static VmPowerState MapPowerState(string powerState) => powerState.ToLowerInvariant() switch
    {
        var s when s.Contains("running") || s.Contains("starting") => VmPowerState.Running,
        var s when s.Contains("deallocat") => VmPowerState.Deallocated,
        _ => VmPowerState.Stopped // stopping / stopped / 未知
    };

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "" : id.Length <= 13 ? id : id[..13] + "…";

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
