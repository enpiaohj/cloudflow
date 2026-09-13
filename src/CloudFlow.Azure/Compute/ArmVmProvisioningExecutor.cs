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
        CancellationToken ct = default)
    {
        var p = request.Payload;
        var vmName = p[CreateVmHandler.PayloadVmName];
        var location = new AzureLocation(p[CreateVmHandler.PayloadRegion]);

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        // ① 资源组（幂等：已存在是安全的空操作，不影响组内现有资源）
        var resourceGroup = await EnsureResourceGroupAsync(armClient, request, location, ct).ConfigureAwait(false);

        // ② 虚拟网络 + ③ 子网（存在则复用真实配置，不存在才用请求里的地址段新建）
        var subnetId = await EnsureSubnetAsync(armClient, resourceGroup, location, p, ct).ConfigureAwait(false);

        var nicName = $"{vmName}-nic";
        var publicIpName = $"{vmName}-public-ip";

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
        ResourceIdentifier? publicIpId = null;
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
                    Subnet = new SubnetData { Id = subnetId },
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

        var nicResourceId = nicOperation.Value.Data.Id;
        logger.LogInformation("ARM create NIC {ResourceId}", nicResourceId);

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
    private async Task<ResourceGroupResource> EnsureResourceGroupAsync(
        ArmClient armClient, OperationRequest request, AzureLocation location, CancellationToken ct)
    {
        var rgName = ParseResourceGroupName(request.ResourceId)
            ?? throw new CloudFlow.Core.Errors.OperationValidationException(
                $"无法从请求解析资源组（请求指向：{request.ResourceId}）。");

        var subscription = armClient.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(request.SubscriptionId));

        var operation = await subscription.GetResourceGroups()
            .CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(location), ct)
            .ConfigureAwait(false);

        logger.LogInformation("确保资源组存在：{Name}", rgName);
        return operation.Value;
    }

    /// <summary>
    /// 确保目标虚拟网络与子网存在，返回可供 NIC 引用的子网 Id。
    /// 虚拟网络输入接受纯名称（在 <paramref name="resourceGroup"/> 下创建/复用）或完整 Resource ID
    /// （跨资源组/订阅复用一个已知存在的虚拟网络——粘贴了具体 ID 就是明确指向已知资源，
    /// 不做存在性判断，真的不存在的话由挂子网这一步的 ARM 调用报 NotFound）。
    /// </summary>
    private async Task<ResourceIdentifier> EnsureSubnetAsync(
        ArmClient armClient, ResourceGroupResource resourceGroup, AzureLocation location,
        IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var vnetInput = p[CreateVmHandler.PayloadVirtualNetwork];
        VirtualNetworkResource vnet;

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
            return existing.Data.Id;
        }

        var subnetPrefix = p.GetValueOrDefault(CreateVmHandler.PayloadSubnetAddressPrefix, "10.0.0.0/24");
        var subnetData = new SubnetData { AddressPrefix = subnetPrefix };
        var subnetOperation = await vnet.GetSubnets()
            .CreateOrUpdateAsync(WaitUntil.Completed, subnetName, subnetData, ct).ConfigureAwait(false);
        logger.LogInformation("新建子网 {Name}（{Prefix}）", subnetName, subnetPrefix);
        return subnetOperation.Value.Data.Id;
    }

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
