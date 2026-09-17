using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// ARM 实现的 VM 磁盘读取（设计文档 §26）。
///
/// 磁盘列表取自 VM 的 storageProfile —— 这是“这台 VM 实际挂着哪些盘”的权威来源；
/// 不按资源组列全部磁盘名去猜，避免把同资源组里别的 VM 的盘算进来。
/// 快照数量用 Resource Graph 按 creationData.sourceResourceId 归到对应磁盘（设计文档 §17：发现用 ARG）。
///
/// 只读实现：快照创建必须经 Operation Engine（disk.snapshot）。
/// </summary>
public sealed class ArmVmDiskService : IVmDiskService
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    /// <summary>按源磁盘列出快照；ARG 的 join 只接受 ==，故在内存里按 sourceResourceId 归并。</summary>
    private const string SnapshotQuery = """
        Resources
        | where type =~ 'microsoft.compute/snapshots'
        | project snapshotId = tostring(id),
                  sourceDiskId = tostring(properties.creationData.sourceResourceId)
        """;

    private static readonly HttpClient Http = new();

    private readonly IAzureClientFactory _clientFactory;
    private readonly ArmAccessTokenProvider _tokenProvider;
    private readonly ScopeContext _scopeContext;
    private readonly ILogger<ArmVmDiskService> _logger;

    public ArmVmDiskService(
        IAzureClientFactory clientFactory,
        ArmAccessTokenProvider tokenProvider,
        ScopeContext scopeContext,
        ILogger<ArmVmDiskService> logger)
    {
        _clientFactory = clientFactory;
        _tokenProvider = tokenProvider;
        _scopeContext = scopeContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VmDiskInfo>> GetDisksAsync(
        string vmResourceId, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(vmResourceId, ct).ConfigureAwait(false);
        var vm = await armClient
            .GetVirtualMachineResource(new ResourceIdentifier(vmResourceId))
            .GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var snapshotCounts = await GetSnapshotCountsAsync(ct).ConfigureAwait(false);
        var profile = vm.Value.Data.StorageProfile;

        var disks = new List<VmDiskInfo>();
        if (profile?.OSDisk is { } osDisk)
        {
            disks.Add(Map(osDisk.Name, "OS Disk", osDisk.DiskSizeGB, osDisk.ManagedDisk, snapshotCounts));
        }

        foreach (var dataDisk in profile?.DataDisks ?? [])
        {
            disks.Add(Map(dataDisk.Name, "Data Disk", dataDisk.DiskSizeGB, dataDisk.ManagedDisk, snapshotCounts));
        }

        _logger.LogInformation("{ResourceId} 读取到 {Count} 块磁盘", vmResourceId, disks.Count);
        return disks;
    }

    public async Task<int> GetSnapshotCountAsync(string vmResourceId, CancellationToken ct = default)
    {
        var disks = await GetDisksAsync(vmResourceId, ct).ConfigureAwait(false);
        return disks.Sum(disk => disk.SnapshotCount);
    }

    private static VmDiskInfo Map(
        string? name,
        string type,
        int? sizeGb,
        VirtualMachineManagedDisk? managedDisk,
        IReadOnlyDictionary<string, int> snapshotCounts)
    {
        var diskId = managedDisk?.Id?.ToString() ?? "";
        return new VmDiskInfo
        {
            DiskId = diskId,
            Name = string.IsNullOrWhiteSpace(name) ? "—" : name,
            Type = type,
            Size = sizeGb is null ? "—" : $"{sizeGb} GB",
            Tier = DescribeStorageAccountType(managedDisk?.StorageAccountType),
            SnapshotCount = snapshotCounts.TryGetValue(diskId, out var count) ? count : 0
        };
    }

    /// <summary>ARM 的 SKU 值（Premium_LRS）直接展示对普通用户不友好，映射为门户里的名称。</summary>
    private static string DescribeStorageAccountType(StorageAccountType? type) => type?.ToString() switch
    {
        "Premium_LRS" => "Premium SSD",
        "PremiumV2_LRS" => "Premium SSD v2",
        "StandardSSD_LRS" => "Standard SSD",
        "StandardSSD_ZRS" => "Standard SSD (ZRS)",
        "Premium_ZRS" => "Premium SSD (ZRS)",
        "Standard_LRS" => "Standard HDD",
        "UltraSSD_LRS" => "Ultra Disk",
        null or "" => "—",
        var other => other
    };

    /// <summary>源磁盘 ID → 该磁盘的快照数。ARG 查询失败不阻断磁盘列表，快照数退化为 0。</summary>
    private async Task<IReadOnlyDictionary<string, int>> GetSnapshotCountsAsync(CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetAsync(ct: ct).ConfigureAwait(false);
            var payload = new Dictionary<string, object> { ["query"] = SnapshotQuery };

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
                    $"快照查询失败（{(int)response.StatusCode}）");
            }

            using var doc = JsonDocument.Parse(body);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var sourceDiskId = GetString(row, "sourceDiskId");
                if (string.IsNullOrEmpty(sourceDiskId))
                {
                    continue;
                }
                counts[sourceDiskId] = counts.TryGetValue(sourceDiskId, out var n) ? n + 1 : 1;
            }
            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "快照数量查询失败，磁盘列表仍返回，快照数显示为 0");
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 按资源所属订阅取凭据：租户跟着订阅走，不能拿"首个已发现订阅"或账户主租户顶替，
    /// 否则客户租户（Guest）下的订阅会取到错的租户 Token。
    /// </summary>
    private async Task<ArmClient> CreateClientAsync(string vmResourceId, CancellationToken ct)
    {
        var account = _scopeContext.ActiveAccount
            ?? throw new NotConfiguredException("尚未选择 Azure 账户。");

        return await _clientFactory
            .CreateAsync(
                ActiveAccountContext.Create(account, _scopeContext, ActiveAccountContext.SubscriptionIdOf(vmResourceId)),
                ct)
            .ConfigureAwait(false);
    }

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
