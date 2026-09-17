using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Identity;
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

    /// <summary>
    /// 完整查询：VM 基本字段 + NIC 关联的专用 IP / 公网 IP。
    /// 专用 IP 取自 NIC 的 IP 配置（VM 的 extended.instanceView 不含 privateIps，
    /// 之前的实现在无公网 IP 的 VM 上会同时丢失专用 IP）。
    /// </summary>
    private const string FullQuery = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend vmKey = tolower(tostring(id)),
                 osType = tostring(properties.storageProfile.osDisk.osType),
                 vmSize = tostring(properties.hardwareProfile.vmSize),
                 powerState = tostring(properties.extended.instanceView.powerState),
                 computerName = tostring(properties.osProfile.computerName),
                 osName = tostring(properties.extended.instanceView.osName),
                 osVersion = tostring(properties.extended.instanceView.osVersion),
                 imageOffer = tostring(properties.storageProfile.imageReference.offer),
                 timeCreated = tostring(properties.timeCreated),
                 hibernationEnabled = tostring(properties.additionalCapabilities.hibernationEnabled)
        | join kind=leftouter (
            Resources
            | where type =~ 'microsoft.network/networkinterfaces'
            | where isnotempty(properties.virtualMachine)
            | project vmId = tolower(tostring(properties.virtualMachine.id)),
                      privateIp = tostring(properties.ipConfigurations[0].properties.privateIPAddress),
                      pipId = tolower(tostring(properties.ipConfigurations[0].properties.publicIPAddress.id))
        ) on $left.vmKey == $right.vmId
        | join kind=leftouter (
            Resources
            | where type =~ 'microsoft.network/publicipaddresses'
            | project pipKey = tolower(tostring(id)), publicIp = tostring(properties.ipAddress)
        ) on $left.pipId == $right.pipKey
        | project name, resourceId = tostring(id), location = tostring(location), resourceGroup, subscriptionId,
                  osType, osName, osVersion, imageOffer, vmSize, powerState, computerName, privateIp,
                  publicIp = tostring(publicIp), timeCreated, hibernationEnabled
        """;

    /// <summary>回退查询：仅 VM 基本字段（join 失败时仍保证 Inventory 可用）。</summary>
    private const string SimpleQuery = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend osType = tostring(properties.storageProfile.osDisk.osType),
                 osName = tostring(properties.extended.instanceView.osName),
                 osVersion = tostring(properties.extended.instanceView.osVersion),
                 imageOffer = tostring(properties.storageProfile.imageReference.offer),
                 vmSize = tostring(properties.hardwareProfile.vmSize),
                 powerState = tostring(properties.extended.instanceView.powerState),
                 computerName = tostring(properties.osProfile.computerName),
                 timeCreated = tostring(properties.timeCreated),
                 hibernationEnabled = tostring(properties.additionalCapabilities.hibernationEnabled)
        | project name, resourceId = tostring(id), location, resourceGroup, subscriptionId,
                  osType, osName, osVersion, imageOffer, vmSize, powerState, computerName, timeCreated,
                  hibernationEnabled
        """;

    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ScopeContext _scopeContext;
    private readonly IVmSizeCatalog _sizeCatalog;
    private readonly ILogger<ResourceGraphVmInventoryService> _logger;
    private static readonly HttpClient Http = new();

    public ResourceGraphVmInventoryService(
        ArmAccessTokenProvider tokenProvider,
        ScopeContext scopeContext,
        IVmSizeCatalog sizeCatalog,
        ILogger<ResourceGraphVmInventoryService> logger)
    {
        _tokenProvider = tokenProvider;
        _scopeContext = scopeContext;
        _sizeCatalog = sizeCatalog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default)
    {
        var token = await _tokenProvider.GetAsync(ct: ct).ConfigureAwait(false);

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

        var vms = MapRows(data);
        await FillSizeInfoAsync(vms, ct).ConfigureAwait(false);
        return vms;
    }

    /// <summary>
    /// 填充内存与 vCPU 核数。ARG 不返回这两项，只能按规格名去规格目录查；同一订阅 + 区域只查一次
    /// （目录内部还有 6 小时缓存）。失败不抛出：两列降级为「—」，不影响列表可用。
    /// </summary>
    private async Task FillSizeInfoAsync(IReadOnlyList<VmSummary> vms, CancellationToken ct)
    {
        var groups = vms
            .Where(vm => !string.IsNullOrEmpty(vm.SubscriptionId)
                         && !string.IsNullOrEmpty(vm.Region)
                         && !string.IsNullOrEmpty(vm.VmSize))
            .GroupBy(vm => (vm.SubscriptionId, vm.Region));

        foreach (var group in groups)
        {
            var sizes = await _sizeCatalog
                .GetBySizeAsync(group.Key.SubscriptionId, group.Key.Region, ct)
                .ConfigureAwait(false);

            if (sizes.Count == 0)
            {
                continue;
            }

            foreach (var vm in group)
            {
                if (sizes.TryGetValue(vm.VmSize, out var info))
                {
                    vm.MemoryMb = info.MemoryMb;
                    vm.VCpuCount = info.VCpus;
                }
            }
        }
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
                OsName = GetString(row, "osName"),
                OsVersion = GetString(row, "osVersion"),
                OsImageOffer = GetString(row, "imageOffer"),
                PowerState = powerState,
                PublicIp = string.IsNullOrEmpty(GetString(row, "publicIp")) ? null : GetString(row, "publicIp"),
                PrivateIp = string.IsNullOrEmpty(privateIp) ? null : privateIp.Split(',')[0].Trim(),
                TimeCreated = ParseTimeCreated(GetString(row, "timeCreated")),
                HibernationEnabled = ParseBool(GetString(row, "hibernationEnabled")),
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

    /// <summary>
    /// 解析 ARG 返回的时间戳。解析不出来返回 null —— 界面据此**不显示该行**，
    /// 而不是显示一个看起来像真的（或像 1970 年）的假时间。
    /// </summary>
    private static DateTimeOffset? ParseTimeCreated(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// 解析布尔字段。ARG 的 <c>tostring()</c> 会产出 "True"/"False"，属性缺失时是空串。
    /// 空串必须映射成 null（"不知道"），不能当成 false —— 那会把"没读到"说成"已禁用"。
    /// </summary>
    private static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null
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
