using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Resources;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 真实磁盘快照执行器：OperationEngine → SnapshotVmHandler → 本执行器
/// → IAzureClientFactory → ArmClient → SnapshotCollection.CreateOrUpdateAsync。
///
/// 源磁盘与目标资源组都从 diskId 解析（Azure Resource ID 是唯一主键）：
/// 快照建在源盘所在的订阅 / 资源组里，跨资源组建快照在 Azure 上不成立。
/// </summary>
public sealed class ArmVmDiskSnapshotExecutor(
    IAzureClientFactory clientFactory,
    ILogger<ArmVmDiskSnapshotExecutor> logger) : IVmDiskSnapshotExecutor
{
    public async Task<string> CreateSnapshotAsync(
        OperationRequest request, string diskId, string snapshotName, CancellationToken ct = default)
    {
        if (!ResourceIdentifier.TryParse(diskId, out var diskResourceId) || diskResourceId is null)
        {
            throw new OperationValidationException($"无效的磁盘 Resource ID：{diskId}");
        }

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        // 源盘必须存在，否则 CreationData 会指向一个空目标，快照创建失败但错误信息很难读
        var sourceDisk = await armClient.GetManagedDiskResource(diskResourceId)
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);

        var location = sourceDisk.Value.Data.Location;
        var resourceGroup = armClient.GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(diskResourceId.SubscriptionId!, diskResourceId.ResourceGroupName));

        var snapshotData = new SnapshotData(location)
        {
            CreationData = new DiskCreationData(DiskCreateOption.Copy)
            {
                SourceResourceId = diskResourceId
            }
        };

        var operation = await resourceGroup.GetSnapshots()
            .CreateOrUpdateAsync(WaitUntil.Completed, snapshotName, snapshotData, ct)
            .ConfigureAwait(false);

        var created = operation.Value.Id.ToString();
        logger.LogInformation(
            "ARM snapshot: {Disk} → {Snapshot} (requestId {RequestId})",
            diskId, created, RequestIdOf(operation.GetRawResponse()));
        return created;
    }

    public async Task<bool> SnapshotExistsAsync(
        OperationRequest request, string snapshotResourceId, CancellationToken ct = default)
    {
        if (!ResourceIdentifier.TryParse(snapshotResourceId, out var id) || id is null)
        {
            return false;
        }

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        try
        {
            var snapshot = await armClient.GetSnapshotResource(id)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false);

            // 只是"资源存在"还不够：ProvisioningState 未成功时快照不可用，
            // 报成功会让用户以为备份已经拿到手。
            return string.Equals(
                snapshot.Value.Data.ProvisioningState?.ToString(),
                "Succeeded",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private static string? RequestIdOf(Response? response) =>
        response is not null && response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
