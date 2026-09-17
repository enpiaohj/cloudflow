using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 真实 Azure 的删除执行器（设计文档 v3.1 §87）：
/// OperationEngine → DeleteVmHandler → 本执行器 → IAzureClientFactory → ArmClient → ARM LRO。
/// </summary>
/// <remarks>
/// <para>
/// 目标资源完全由 <c>request.ResourceId</c> 解析（Azure Resource ID 是唯一主键），不从 Payload 取名称。
/// </para>
/// <para>
/// 连带删除的资源由本类<b>读回真实挂载关系</b>得到，Handler 再与请求求交集 ——
/// 本执行器只删交集里那些，<b>不自行扩大范围</b>。
/// </para>
/// </remarks>
public sealed class ArmVmDeleteExecutor(
    IAzureClientFactory clientFactory,
    ILogger<ArmVmDeleteExecutor> logger) : IVmDeleteExecutor
{
    public async Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
        OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var vmId = ArmVmTarget.ParseVmId(request);

        VirtualMachineData data;
        try
        {
            // GetAsync 的首参是 InstanceViewType?，用命名参数跳过它（与 ArmVmPowerExecutor 同一写法）
            data = (await armClient.GetVirtualMachineResource(vmId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Data;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 虚拟机已经不在了 = 没有可连带的资源。调用方（Handler）会先判存在性，
            // 这里兜住"影响分析读过之后、执行之前它被别处删掉"的竞态 ——
            // 否则这个原始异常会以一句无意义的 ARM 错误冒到用户面前。
            return [];
        }

        var linked = new List<VmLinkedResource>();

        // ── 磁盘 ──
        if (data.StorageProfile?.OSDisk?.ManagedDisk?.Id is { } osDiskId)
        {
            linked.Add(new(osDiskId.ToString(), VmLinkedResourceKind.OsDisk,
                $"{ArmVmTarget.NameOf(osDiskId)}（OS 磁盘）"));
        }

        if (data.StorageProfile?.DataDisks is { } dataDisks)
        {
            foreach (var disk in dataDisks)
            {
                if (disk.ManagedDisk?.Id is { } dataDiskId)
                {
                    linked.Add(new(dataDiskId.ToString(), VmLinkedResourceKind.DataDisk,
                        $"{ArmVmTarget.NameOf(dataDiskId)}（数据磁盘）"));
                }
            }
        }

        // ── 网卡，以及挂在网卡上的公网 IP ──
        if (data.NetworkProfile?.NetworkInterfaces is { } networkInterfaces)
        {
            foreach (var nicReference in networkInterfaces)
            {
                if (nicReference.Id is not { } nicId)
                {
                    continue;
                }

                linked.Add(new(nicId.ToString(), VmLinkedResourceKind.NetworkInterface,
                    $"{ArmVmTarget.NameOf(nicId)}（网卡）"));

                foreach (var publicIpId in await GetPublicIpIdsAsync(armClient, nicId, ct).ConfigureAwait(false))
                {
                    linked.Add(new(publicIpId.ToString(), VmLinkedResourceKind.PublicIpAddress,
                        $"{ArmVmTarget.NameOf(publicIpId)}（公网 IP）"));
                }
            }
        }

        return linked;
    }

    public async Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var vmId = ArmVmTarget.ParseVmId(request);

        // DeleteAsync 的第二个位置参数是 bool? forceDeletion（强制删除带加密盘的 VM），
        // 这里不强制，因此用命名参数直接给 CancellationToken。
        var operation = await armClient.GetVirtualMachineResource(vmId)
            .DeleteAsync(WaitUntil.Completed, cancellationToken: ct).ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM delete VM {ResourceId} (requestId {RequestId})",
            request.ResourceId, requestId);
        return requestId;
    }

    public async Task<bool> DeleteLinkedAsync(
        OperationRequest request, VmLinkedResource resource, CancellationToken ct = default)
    {
        if (!ResourceIdentifier.TryParse(resource.ResourceId, out var resourceId) || resourceId is null)
        {
            logger.LogWarning("连带资源 {ResourceId} 的 ID 无效，跳过", resource.ResourceId);
            return false;
        }

        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);

        try
        {
            await DeleteByKindAsync(armClient, resourceId, resource.Kind, ct).ConfigureAwait(false);
            logger.LogInformation("ARM delete linked {ResourceId}", resource.ResourceId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 已经不在了 = 目标已达成（可能上一轮删过，或 Azure 侧自行清理）
            return true;
        }
        catch (RequestFailedException ex)
        {
            // 返回 false 而不是抛：调用方（Handler）要把它汇总成一句
            // 「虚拟机已删，但这些还在」——而抛出 Azure 的异常类型会把 Provider 细节
            // 漏进本该与 Provider 无关的 Handler。
            logger.LogWarning(ex, "连带删除 {ResourceId} 失败", resource.ResourceId);
            return false;
        }
    }

    public async Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var vmId = ArmVmTarget.ParseVmId(request);

        try
        {
            await armClient.GetVirtualMachineResource(vmId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// 读网卡上挂的公网 IP。
    /// 读不到<b>不算失败</b>：少列一项"可连带资源"而已 —— 真正删不掉会在 Execute 阶段报出来，
    /// 那才是该让用户看到的地方。
    /// </summary>
    private async Task<IReadOnlyList<ResourceIdentifier>> GetPublicIpIdsAsync(
        ArmClient armClient, ResourceIdentifier nicId, CancellationToken ct)
    {
        try
        {
            var nic = (await armClient.GetNetworkInterfaceResource(nicId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value;

            if (nic.Data.IPConfigurations is not { } configurations)
            {
                return [];
            }

            return
            [
                .. configurations
                    .Select(configuration => configuration.PublicIPAddress?.Id)
                    .Where(id => id is not null)
                    .Select(id => id!)
            ];
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "读取网卡 {NicId} 的公网 IP 失败，本次影响分析将不含公网 IP", nicId);
            return [];
        }
    }

    /// <summary>按类别删除一件连带资源。类别来自我们自己读回的挂载关系，因此不会有"未知类型"。</summary>
    private static async Task DeleteByKindAsync(
        ArmClient armClient, ResourceIdentifier resourceId, VmLinkedResourceKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case VmLinkedResourceKind.NetworkInterface:
                await armClient.GetNetworkInterfaceResource(resourceId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
                break;

            case VmLinkedResourceKind.PublicIpAddress:
                await armClient.GetPublicIPAddressResource(resourceId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
                break;

            default:
                await armClient.GetManagedDiskResource(resourceId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<ArmClient> CreateClientAsync(OperationRequest request, CancellationToken ct) =>
        await clientFactory.CreateAsync(RequestCredential.From(request), ct).ConfigureAwait(false);

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
