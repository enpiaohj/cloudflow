using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的虚拟机详情读取：一次 <c>GET .../virtualMachines/{name}</c>，
/// 外加两条辅助数据源（规格目录、DevTestLab 关闭计划）。
///
/// 为什么规格值要另找规格目录：Azure 的 VM 资源只有 <c>hardwareProfile.vmSize</c> 一个规格名，
/// vCPU / 内存 / 每个核心的线程数都不在它上面，只能按 订阅+区域 去 SKU 表查（已带 6 小时缓存）。
///
/// 为什么关闭计划要另发一次 Resource Graph 查询：<c>Microsoft.DevTestLab/schedules</c> 是**独立资源**，
/// 可以放在任意资源组，只有按 <c>properties.targetResourceId</c> 反查才找得到。
/// 查询失败**不**让整个详情读取失败 —— 关闭计划是附加信息，缺失时该行不显示即可。
/// </summary>
public sealed class ArmVmDetailService(
    IAzureClientFactory clientFactory,
    ArmAccessTokenProvider tokenProvider,
    ScopeContext scopeContext,
    IVmSizeCatalog sizeCatalog,
    ILogger<ArmVmDetailService> logger) : IVmDetailService
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 按目标 VM 反查自动关闭计划。别名不能用 <c>time</c>（KQL 保留字，实测报
    /// ParserFailure），所以写成 <c>properties.dailyRecurrence['time']</c> + 别名 shutdownTime。
    /// </summary>
    private const string AutoShutdownQueryTemplate = """
        Resources
        | where type =~ 'microsoft.devtestlab/schedules'
        | where tolower(tostring(properties.targetResourceId)) == '{0}'
        | project status = tostring(properties.status),
                  taskType = tostring(properties.taskType),
                  shutdownTime = tostring(properties.dailyRecurrence['time']),
                  timeZoneId = tostring(properties.timeZoneId)
        """;

    public async Task<VmDetailInfo?> GetAsync(string vmResourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmResourceId))
        {
            return null;
        }

        var armClient = await CreateClientAsync(vmResourceId, ct).ConfigureAwait(false);
        var resourceId = new ResourceIdentifier(vmResourceId);

        VirtualMachineData data;
        try
        {
            var response = await armClient.GetVirtualMachineResource(resourceId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false);
            data = response.Value.Data;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 404 = 这台 VM 在 ARM 里确实不存在（已删除）。这不是"读取失败"，返回 null 让调用方区分。
            return null;
        }
        catch (RequestFailedException ex)
        {
            // 其余失败（403 / 429 / 5xx）必须冒泡成 CloudFlowException，
            // 否则上层会把"没权限"当成"这台机器没有详情"。
            throw new CloudFlowException(
                CloudFlowErrorCode.AzureError,
                $"读取虚拟机详情失败（HTTP {ex.Status}）：{ex.Message}", ex);
        }

        var subscriptionId = ActiveAccountContext.SubscriptionIdOf(vmResourceId);
        // AzureLocation 是 struct，缺失时 Name 为空串而不是 null
        var location = data.Location.Name;
        var vmSize = data.HardwareProfile?.VmSize?.ToString();

        var (vcpus, memoryMb, vcpusPerCore) = await ResolveSizeValuesAsync(
            subscriptionId, location, vmSize, ct).ConfigureAwait(false);

        var (autoShutdownEnabled, scheduledShutdownText) = await ResolveAutoShutdownAsync(
            vmResourceId, ct).ConfigureAwait(false);

        var securityProfile = data.SecurityProfile;

        return new VmDetailInfo
        {
            VmSize = vmSize,
            VCpus = vcpus,
            MemoryMb = memoryMb,
            VCpusPerCore = vcpusPerCore,
            ProvisioningState = data.ProvisioningState,
            TimeCreated = data.TimeCreated,
            HibernationEnabled = data.AdditionalCapabilities?.HibernationEnabled,

            OsType = data.OSProfile?.WindowsConfiguration is not null ? "Windows"
                : data.OSProfile?.LinuxConfiguration is not null ? "Linux"
                : data.StorageProfile?.OSDisk?.OSType?.ToString(),
            ComputerName = data.OSProfile?.ComputerName,
            AdminUsername = data.OSProfile?.AdminUsername,

            SecurityType = securityProfile?.SecurityType?.ToString(),
            SecureBootEnabled = securityProfile?.UefiSettings?.IsSecureBootEnabled,
            VTpmEnabled = securityProfile?.UefiSettings?.IsVirtualTpmEnabled,
            // Compute SDK 1.16.0 的 UefiSettings 只有 SecureBoot / VTpm 两个成员，没有完整性监视。
            // 实测 ARM 也不返回该字段（vm-01 的 uefiSettings 只有那两项），所以这里保持 null，
            // 界面显示 "—"。门户把它显示成"已禁用"是把缺失当 false，本应用不跟着编。
            IntegrityMonitoringEnabled = null,

            VmId = data.VmId is { } id ? id.ToString() : null,

            AutoShutdownEnabled = autoShutdownEnabled,
            ScheduledShutdownText = scheduledShutdownText,

            Zones = [.. data.Zones ?? []],
            AvailabilitySetName = data.AvailabilitySetId?.Name,
        };
    }

    /// <summary>
    /// 规格名 → vCPU / 内存 / 每核线程数。查不到就三项都为 null，界面不显示对应行 ——
    /// 不用规格名里的数字猜（Standard_B2als_v2 的 B2 与 2 vCPU 只是巧合，不构成约定）。
    /// </summary>
    private async Task<(int? VCpus, int? MemoryMb, int? VCpusPerCore)> ResolveSizeValuesAsync(
        string? subscriptionId,
        string? location,
        string? vmSize,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(vmSize) || string.IsNullOrEmpty(location) || string.IsNullOrEmpty(subscriptionId))
        {
            return (null, null, null);
        }

        try
        {
            var sizes = await sizeCatalog.GetBySizeAsync(subscriptionId, location, ct).ConfigureAwait(false);
            if (sizes.TryGetValue(vmSize, out var info))
            {
                return (info.VCpus, info.MemoryMb, info.VCpusPerCore);
            }
        }
        catch (Exception ex)
        {
            // 规格目录自己已经降级过一次；这里再兜一层，保证规格查不到不会让整页详情读不出来
            logger.LogWarning(ex, "规格 {VmSize} 的规格值查询失败，vCPU / 内存 / 每核线程数显示为 —", vmSize);
        }

        return (null, null, null);
    }

    /// <summary>
    /// 自动关闭计划。返回 (null, null) 表示"没查到"（查询失败），
    /// 返回 (false, null) 表示"查到了，确认没有计划" —— 两者在界面上都是"未启用"或"不显示"，
    /// 但语义不同，所以不合并。
    /// </summary>
    private async Task<(bool? Enabled, string? ScheduledText)> ResolveAutoShutdownAsync(
        string vmResourceId, CancellationToken ct)
    {
        var subscriptionId = ActiveAccountContext.SubscriptionIdOf(vmResourceId);
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return (null, null);
        }

        try
        {
            var token = await tokenProvider.GetAsync(subscriptionId, ct).ConfigureAwait(false);
            var query = string.Format(
                AutoShutdownQueryTemplate,
                vmResourceId.ToLowerInvariant().Replace("'", "''", StringComparison.Ordinal));
            var rows = await QueryResourceGraphAsync(token, subscriptionId, query, ct).ConfigureAwait(false);

            // 一个 VM 可能有多个 schedule 资源（关闭 + 启动），只有 shutdown 那条算"自动关闭"
            foreach (var row in rows.EnumerateArray())
            {
                var taskType = GetString(row, "taskType");
                if (taskType.Contains("Shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, FormatSchedule(row));
                }
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "自动关闭计划查询失败（{VmResourceId}），该行不显示", vmResourceId);
            return (null, null);
        }
    }

    private static string? FormatSchedule(JsonElement row)
    {
        var shutdownTime = GetString(row, "shutdownTime");
        var timeZone = GetString(row, "timeZoneId");
        return shutdownTime switch
        {
            "" => null,
            _ when timeZone == "" => shutdownTime,
            _ => $"{shutdownTime} ({timeZone})"
        };
    }

    private static async Task<JsonElement> QueryResourceGraphAsync(
        string token, string subscriptionId, string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = query,
            ["subscriptions"] = new[] { subscriptionId }
        });

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

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// 按资源所属订阅取凭据：租户跟着订阅走，不能拿"首个已发现订阅"或账户主租户顶替，
    /// 否则客户租户（Guest）下的订阅会取到错的租户 Token。
    /// </summary>
    private async Task<ArmClient> CreateClientAsync(string vmResourceId, CancellationToken ct)
    {
        var account = scopeContext.ActiveAccount
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");

        return await clientFactory
            .CreateAsync(
                ActiveAccountContext.Create(account, scopeContext, ActiveAccountContext.SubscriptionIdOf(vmResourceId)),
                ct)
            .ConfigureAwait(false);
    }
}
