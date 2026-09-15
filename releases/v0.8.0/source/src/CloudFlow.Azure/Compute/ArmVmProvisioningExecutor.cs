using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 真实 Azure 的创建执行器（设计文档 v3.1 §87，v3.2 起支持按需新建资源组 / 虚拟网络 / 子网）：
/// OperationEngine → CreateVmHandler → 本执行器 → IAzureClientFactory → ArmClient → ARM LRO。
/// </summary>
/// <remarks>
/// <para>
/// <b>创建顺序不可颠倒</b>：资源组 → 虚拟网络 → 子网 → 公网 IP（可选）→ NIC → VM。
/// 后面每一步都依赖前一步产出的 Id；NIC 引用公网 IP、VM 引用 NIC，反过来必然 NotFound。
/// OS 盘随 VM 的 StorageProfile 隐式创建，且 <see cref="VirtualMachineOSDisk.DeleteOption"/>
/// 设为 Delete —— 删除这台 VM 时 OS 盘一并删除，从源头减少孤儿盘。
/// </para>
/// <para>
/// <b>资源组 / 虚拟网络 / 子网按幂等 ensure-exists 处理</b>（v3.2 起，§87 收窄范围已放开这三项）：
/// 不存在则用请求里给的地址段新建；<b>已存在则完全不碰其配置</b>，只取其 Id 挂后续资源——
/// 这与本文件对 VM / NIC / 公网 IP"先查重名再创建"的既有纪律是同一种谨慎，只是方向相反
/// （那三个是"存在则拒绝，必须是全新名字"，这三个是"存在则复用，不做全新校验"）。
/// NSG 仍不创建。
/// </para>
/// </remarks>
public sealed class ArmVmProvisioningExecutor(
    IAzureClientFactory clientFactory,
    ILogger<ArmVmProvisioningExecutor> logger) : IVmProvisioningExecutor
{
    public async Task<string?> CreateAsync(
        OperationRequest request,
        Func<CancellationToken, Task<string?>>? resolvePassword,
        Func<string, CancellationToken, Task> reportProgress,
        CancellationToken ct = default)
    {
        var p = request.Payload;
        var vmName = p[CreateVmHandler.PayloadVmName];
        var location = new AzureLocation(p[CreateVmHandler.PayloadRegion]);

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        var nicName = $"{vmName}-nic";
        var publicIpName = $"{vmName}-public-ip";

        // 从资源组这一步开始，任何一步失败都要：① 把本次已经新建出来的资源清理掉（否则用户只会
        // 看到"创建失败"，却在 Azure 门户里发现资源组/网络/网卡凭空多了出来，得自己找出来手动删），
        // ② 把 RequestFailedException 的原始 HTTP 转储摘成一句人话——所以连"确保资源组/虚拟网络/
        // 子网存在"这两步也必须在 try 里面，不能只包后面的 VM/NIC/公网 IP（这两步一样会报错，
        // 比如资源组已存在于别的区域时的 409 InvalidResourceGroupLocation）。
        // 复用的既有资源（rgCreated/subnet.VnetCreated/subnet.SubnetCreated 为 false 的那些）绝不碰。
        ResourceGroupResource? resourceGroup = null;
        var rgCreated = false;
        SubnetProvisioningResult? subnet = null;
        ResourceIdentifier? publicIpId = null;
        ResourceIdentifier? nicResourceId = null;

        try
        {
            // ① 资源组（幂等：已存在是安全的空操作，不影响组内现有资源）
            (resourceGroup, rgCreated) = await EnsureResourceGroupAsync(armClient, request, location, ct)
                .ConfigureAwait(false);
            await reportProgress(rgCreated ? "资源组已创建" : "资源组已就绪（复用）", ct).ConfigureAwait(false);

            // ② 虚拟网络 + ③ 子网（存在则复用真实配置，不存在才用请求里的地址段新建）
            subnet = await EnsureSubnetAsync(armClient, resourceGroup, location, p, ct).ConfigureAwait(false);
            await reportProgress("虚拟网络/子网已就绪", ct).ConfigureAwait(false);

            // ARM 的 CreateOrUpdateAsync 会覆盖同名资源。创建操作绝不能把它当作“可更新”，
            // 所以在任何写入发生前，先拒绝目标 VM 以及本次派生 NIC / 公网 IP 名称的冲突。
            if ((await resourceGroup.GetVirtualMachines()
                    .ExistsAsync(vmName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value)
            {
                throw new CloudFlow.Core.Errors.OperationValidationException($"虚拟机「{vmName}」已存在，不能覆盖创建。");
            }

            if ((await resourceGroup.GetNetworkInterfaces()
                    .ExistsAsync(nicName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value)
            {
                throw new CloudFlow.Core.Errors.OperationValidationException($"网卡「{nicName}」已存在，不能覆盖创建。");
            }

            var withPublicIp = string.Equals(
                p.GetValueOrDefault(CreateVmHandler.PayloadPublicIp), "true", StringComparison.OrdinalIgnoreCase);
            if (withPublicIp && (await resourceGroup.GetPublicIPAddresses()
                    .ExistsAsync(publicIpName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value)
            {
                throw new CloudFlow.Core.Errors.OperationValidationException($"公网 IP「{publicIpName}」已存在，不能覆盖创建。");
            }

            // ④ 公网 IP（可选）
            if (withPublicIp)
            {
                var publicIpData = new PublicIPAddressData
                {
                    Location = location,
                    // Basic SKU 已退役；Standard SKU 必须使用静态分配。
                    PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                    Sku = new PublicIPAddressSku { Name = PublicIPAddressSkuName.Standard }
                };

                var publicIpOperation = await resourceGroup
                    .GetPublicIPAddresses()
                    .CreateOrUpdateAsync(WaitUntil.Completed, publicIpName, publicIpData, ct)
                    .ConfigureAwait(false);

                publicIpId = publicIpOperation.Value.Data.Id;
                logger.LogInformation("ARM create public IP {ResourceId}", publicIpId);
                await reportProgress("公网 IP 已创建", ct).ConfigureAwait(false);
            }

            // ⑤ NIC（挂上面解析出的子网 + 可选公网 IP）
            var nicData = new NetworkInterfaceData
            {
                Location = location,
                IPConfigurations =
                {
                    new NetworkInterfaceIPConfigurationData
                    {
                        Name = "ipconfig1",
                        Primary = true,
                        Subnet = new SubnetData { Id = subnet.SubnetId },
                    }
                }
            };

            if (publicIpId is { } ipId)
            {
                nicData.IPConfigurations[0].PublicIPAddress = new PublicIPAddressData { Id = ipId };
            }

            var nicOperation = await resourceGroup
                .GetNetworkInterfaces()
                .CreateOrUpdateAsync(WaitUntil.Completed, nicName, nicData, ct)
                .ConfigureAwait(false);

            nicResourceId = nicOperation.Value.Data.Id;
            logger.LogInformation("ARM create NIC {ResourceId}", nicResourceId);
            await reportProgress("网卡已创建，正在创建虚拟机…", ct).ConfigureAwait(false);

            // ⑥ VM（OS 盘随 StorageProfile 隐式创建，DeleteOption=Delete 使其随 VM 一并删除）
            var authType = p.GetValueOrDefault(CreateVmHandler.PayloadAuthType, "ssh");
            var adminUsername = p[CreateVmHandler.PayloadAdminUsername];

            var isWindowsImage = p[CreateVmHandler.PayloadImage]
                .StartsWith("MicrosoftWindows", StringComparison.OrdinalIgnoreCase);
            var osProfile = new VirtualMachineOSProfile
            {
                ComputerName = vmName,
                AdminUsername = adminUsername
            };

            if (string.Equals(authType, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                osProfile.LinuxConfiguration = new LinuxConfiguration
                {
                    DisablePasswordAuthentication = true,
                    SshPublicKeys =
                    {
                        new SshPublicKeyConfiguration
                        {
                            KeyData = p[CreateVmHandler.PayloadSshPublicKey],
                            Path = $"/home/{adminUsername}/.ssh/authorized_keys"
                        }
                    }
                };
            }
            else
            {
                // 密码方式：凭 Id 解出明文 —— 委托由 App 层路由器注入，这里即取即用
                var password = resolvePassword is null ? null : await resolvePassword(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(password))
                {
                    throw new CloudFlow.Core.Errors.OperationValidationException(
                        "密码方式创建失败：无法从凭据库解出管理员密码（凭据可能已被删除）。");
                }

                osProfile.AdminPassword = password;
                if (isWindowsImage)
                {
                    osProfile.WindowsConfiguration = new WindowsConfiguration
                    {
                        ProvisionVmAgent = true
                    };
                }
                else
                {
                    osProfile.LinuxConfiguration = new LinuxConfiguration
                    {
                        DisablePasswordAuthentication = false
                    };
                }
            }

            var imageParts = p[CreateVmHandler.PayloadImage].Split(':');
            var vmData = new VirtualMachineData(location)
            {
                HardwareProfile = new VirtualMachineHardwareProfile
                {
                    VmSize = p[CreateVmHandler.PayloadVmSize]
                },
                StorageProfile = new VirtualMachineStorageProfile
                {
                    ImageReference = new ImageReference
                    {
                        Publisher = imageParts[0],
                        Offer = imageParts[1],
                        Sku = imageParts[2],
                        Version = imageParts[3]
                    },
                    OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                    {
                        DeleteOption = DiskDeleteOptionType.Delete
                    }
                },
                OSProfile = osProfile,
                NetworkProfile = new VirtualMachineNetworkProfile
                {
                    NetworkInterfaces =
                    {
                        new VirtualMachineNetworkInterfaceReference { Id = nicResourceId, Primary = true }
                    }
                }
            };

            var vmOperation = await resourceGroup
                .GetVirtualMachines()
                .CreateOrUpdateAsync(WaitUntil.Completed, vmName, vmData, ct)
                .ConfigureAwait(false);

            var requestId = RequestIdOf(vmOperation.GetRawResponse());
            logger.LogInformation("ARM create VM {Name} (requestId {RequestId})", vmName, requestId);
            return requestId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // resourceGroup 为 null 说明连"确保资源组存在"都没成功，这次尝试什么都没新建，无需回滚。
            if (resourceGroup is not null)
            {
                await RollBackAsync(armClient, resourceGroup, rgCreated, subnet, publicIpId, nicResourceId, ct)
                    .ConfigureAwait(false);
            }

            if (ex is RequestFailedException rfe)
            {
                throw new CloudFlow.Core.Errors.CloudFlowException(
                    CloudFlow.Core.Errors.CloudFlowErrorCode.AzureError,
                    AzureErrorMessages.Summarize(rfe), rfe);
            }

            throw;
        }
    }

    /// <summary>
    /// 失败回滚：只清理这次尝试**真正新建**的资源，绝不碰复用的既有资源。
    /// 资源组是本次新建时直接删整个资源组（连带虚拟网络/子网一起清掉，最彻底）；
    /// 否则按 NIC → 公网 IP → 虚拟网络/子网 的顺序单独清理——NIC 引用了后两者，必须先删它，
    /// 顺序反了会拿到 Azure 的 "InUseByResource"。
    /// 回滚本身失败只记日志，不能让"清理失败"盖过用户真正需要看到的原始报错。
    /// </summary>
    private async Task RollBackAsync(
        ArmClient armClient, ResourceGroupResource resourceGroup, bool rgCreated,
        SubnetProvisioningResult? subnet, ResourceIdentifier? publicIpId, ResourceIdentifier? nicId,
        CancellationToken ct)
    {
        try
        {
            if (rgCreated)
            {
                logger.LogWarning("创建虚拟机失败，回滚：删除本次新建的资源组 {Name}", resourceGroup.Data.Name);
                await resourceGroup.DeleteAsync(WaitUntil.Completed, cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            if (nicId is not null)
            {
                logger.LogWarning("创建虚拟机失败，回滚：删除本次新建的网卡 {Id}", nicId);
                await armClient.GetNetworkInterfaceResource(nicId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
            }

            if (publicIpId is not null)
            {
                logger.LogWarning("创建虚拟机失败，回滚：删除本次新建的公网 IP {Id}", publicIpId);
                await armClient.GetPublicIPAddressResource(publicIpId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
            }

            // subnet 为 null 说明连"确保虚拟网络/子网存在"这步都没走到（比如资源组本身就建失败了），
            // 没有网络层面的东西需要清理。
            if (subnet is null)
            {
                return;
            }

            if (subnet.VnetCreated)
            {
                logger.LogWarning("创建虚拟机失败，回滚：删除本次新建的虚拟网络 {Name}（连带其子网）",
                    subnet.Vnet.Data.Name);
                await subnet.Vnet.DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
            }
            else if (subnet.SubnetCreated)
            {
                logger.LogWarning("创建虚拟机失败，回滚：删除本次新建的子网 {Id}", subnet.SubnetId);
                await armClient.GetSubnetResource(subnet.SubnetId)
                    .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);
            }
        }
        catch (Exception cleanupEx)
        {
            // 回滚失败通常意味着确实还残留了资源，只能如实记录、留给用户去门户核实；
            // 不能因为清理失败就把原始的创建失败原因吞掉。
            logger.LogError(cleanupEx,
                "创建虚拟机失败后自动清理也失败了，资源组 {ResourceGroup} 下可能残留资源，需要手动核实",
                resourceGroup.Data.Name);
        }
    }

    public async Task<bool> VmReadyAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        var vmId = ArmVmTarget.ParseVmId(request);

        try
        {
            var vm = (await armClient.GetVirtualMachineResource(vmId)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            return string.Equals(vm.Data.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<bool> ResourceGroupExistsAsync(OperationRequest request, CancellationToken ct = default)
    {
        var rgName = ParseResourceGroupName(request.ResourceId);
        if (rgName is null)
        {
            return false;
        }

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        var subscription = armClient.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(request.SubscriptionId));

        return (await subscription.GetResourceGroups()
            .ExistsAsync(rgName, ct).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// 确保目标资源组存在：不存在则以 <see cref="CreateVmHandler.PayloadRegion"/> 新建，
    /// 已存在则直接复用（<c>CreateOrUpdateAsync</c> 对同位置的已有资源组是安全的空操作；
    /// 若已有资源组的实际位置与本次请求的区域不同，ARM 会在这一步报错，交由引擎按失败处理）。
    /// </summary>
    private async Task<(ResourceGroupResource ResourceGroup, bool Created)> EnsureResourceGroupAsync(
        ArmClient armClient, OperationRequest request, AzureLocation location, CancellationToken ct)
    {
        var rgName = ParseResourceGroupName(request.ResourceId)
            ?? throw new CloudFlow.Core.Errors.OperationValidationException(
                $"无法从请求解析资源组（请求指向：{request.ResourceId}）。");

        var subscription = armClient.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(request.SubscriptionId));

        var existed = (await subscription.GetResourceGroups()
            .ExistsAsync(rgName, ct).ConfigureAwait(false)).Value;

        // 已存在就直接复用、不再调 CreateOrUpdate——资源组一旦建好，区域是不可变的；
        // 之前这里不管存不存在都会带着请求里的区域去 CreateOrUpdate，如果已有资源组实际在别的区域，
        // 会白白撞上 Azure 的 409 InvalidResourceGroupLocation（哪怕对这台资源组而言什么都不需要改）。
        // 与虚拟网络/子网同一条纪律：存在则只取引用，不拿本次的输入去覆盖它。
        if (existed)
        {
            var existing = await subscription.GetResourceGroups().GetAsync(rgName, ct).ConfigureAwait(false);
            logger.LogInformation("确保资源组存在：{Name}（复用，实际区域 {Location}）",
                rgName, existing.Value.Data.Location);
            return (existing.Value, false);
        }

        var operation = await subscription.GetResourceGroups()
            .CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(location), ct)
            .ConfigureAwait(false);

        logger.LogInformation("确保资源组存在：{Name}（新建，区域 {Location}）", rgName, location);
        return (operation.Value, true);
    }

    /// <summary>
    /// 确保目标虚拟网络与子网存在，返回可供 NIC 引用的子网 Id。
    /// 虚拟网络输入接受纯名称（在 <paramref name="resourceGroup"/> 下创建/复用）或完整 Resource ID
    /// （跨资源组/订阅复用一个已知存在的虚拟网络——粘贴了具体 ID 就是明确指向已知资源，
    /// 不做存在性判断，真的不存在的话由挂子网这一步的 ARM 调用报 NotFound）。
    /// </summary>
    private async Task<SubnetProvisioningResult> EnsureSubnetAsync(
        ArmClient armClient, ResourceGroupResource resourceGroup, AzureLocation location,
        IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var vnetInput = p[CreateVmHandler.PayloadVirtualNetwork];
        VirtualNetworkResource vnet;
        var vnetCreated = false;

        if (vnetInput.Contains("/virtualNetworks/", StringComparison.OrdinalIgnoreCase) &&
            ResourceIdentifier.TryParse(vnetInput, out var vnetId) && vnetId is not null)
        {
            vnet = armClient.GetVirtualNetworkResource(vnetId);
        }
        else
        {
            var vnetName = vnetInput;
            var exists = (await resourceGroup.GetVirtualNetworks()
                .ExistsAsync(vnetName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value;

            if (exists)
            {
                vnet = (await resourceGroup.GetVirtualNetworks()
                    .GetAsync(vnetName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value;
                logger.LogInformation("复用已有虚拟网络 {Name}，不改动其地址空间", vnetName);
            }
            else
            {
                var addressSpace = p.GetValueOrDefault(CreateVmHandler.PayloadVnetAddressSpace, "10.0.0.0/16");
                var vnetData = new VirtualNetworkData
                {
                    Location = location,
                    AddressPrefixes = { addressSpace }
                };

                var vnetOperation = await resourceGroup.GetVirtualNetworks()
                    .CreateOrUpdateAsync(WaitUntil.Completed, vnetName, vnetData, ct).ConfigureAwait(false);
                vnet = vnetOperation.Value;
                vnetCreated = true;
                logger.LogInformation("新建虚拟网络 {Name}（{AddressSpace}）", vnetName, addressSpace);
            }
        }

        var subnetName = p[CreateVmHandler.PayloadSubnetName];
        var subnetExists = (await vnet.GetSubnets()
            .ExistsAsync(subnetName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value;

        if (subnetExists)
        {
            var existing = (await vnet.GetSubnets()
                .GetAsync(subnetName, expand: null, cancellationToken: ct).ConfigureAwait(false)).Value;
            logger.LogInformation("复用已有子网 {Name}（实际地址段 {Prefix}），不改动其配置",
                subnetName, existing.Data.AddressPrefix);
            return new SubnetProvisioningResult(existing.Data.Id, vnet, vnetCreated, SubnetCreated: false);
        }

        var subnetPrefix = p.GetValueOrDefault(CreateVmHandler.PayloadSubnetAddressPrefix, "10.0.0.0/24");
        var subnetData = new SubnetData { AddressPrefix = subnetPrefix };
        var subnetOperation = await vnet.GetSubnets()
            .CreateOrUpdateAsync(WaitUntil.Completed, subnetName, subnetData, ct).ConfigureAwait(false);
        logger.LogInformation("新建子网 {Name}（{Prefix}）", subnetName, subnetPrefix);
        return new SubnetProvisioningResult(subnetOperation.Value.Data.Id, vnet, vnetCreated, SubnetCreated: true);
    }

    /// <summary>
    /// <paramref name="VnetCreated"/>/<paramref name="SubnetCreated"/> 供失败回滚用：
    /// 只有这次真正新建出来的资源才需要在后续步骤失败时清理，复用的既有资源绝不能碰。
    /// </summary>
    private sealed record SubnetProvisioningResult(
        ResourceIdentifier SubnetId, VirtualNetworkResource Vnet, bool VnetCreated, bool SubnetCreated);

    /// <summary>从形如 …/resourceGroups/{rg}/… 的 Resource ID 里解析资源组名。</summary>
    private static string? ParseResourceGroupName(string resourceId) =>
        ResourceIdentifier.TryParse(resourceId, out var id) && id is not null
            ? id.ResourceGroupName
            : null;

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
