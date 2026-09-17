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
/// 真实 Azure 的创建执行器（设计文档 v3.1 §87）：
/// OperationEngine → CreateVmHandler → 本执行器 → IAzureClientFactory → ArmClient → ARM LRO。
/// </summary>
/// <remarks>
/// <para>
/// <b>创建顺序不可颠倒</b>：公网 IP（可选）→ NIC → VM。
/// NIC 引用公网 IP、VM 引用 NIC，反过来必然 NotFound。OS 盘随 VM 的 StorageProfile 隐式创建，
/// 且 <see cref="VirtualMachineOSDisk.DeleteOption"/> 设为 Delete —— 删除这台 VM 时 OS 盘一并删除，
/// 从源头减少孤儿盘。
/// </para>
/// <para>
/// 资源组与子网必须<b>已存在</b>（§87 明确不做资源组/网络创建）；不存在时 ARM 返回 NotFound，
/// 由引擎按失败处理并如实报告。
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
        var subnetIdText = p[CreateVmHandler.PayloadSubnetId];

        if (!ResourceIdentifier.TryParse(subnetIdText, out var subnetId) || subnetId is null)
        {
            throw new CloudFlow.Core.Errors.OperationValidationException($"子网 Resource ID 无效：{subnetIdText}");
        }

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        var resourceGroup = GetResourceGroup(armClient, request);
        var location = new AzureLocation(p[CreateVmHandler.PayloadRegion]);
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

        // ① 公网 IP（可选）
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

        // ② NIC（挂已有子网 + 可选公网 IP）
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

        // ③ VM（OS 盘随 StorageProfile 隐式创建，DeleteOption=Delete 使其随 VM 一并删除）
        var authType = p.GetValueOrDefault(CreateVmHandler.PayloadAuthType, "ssh");
        var adminUsername = p[CreateVmHandler.PayloadAdminUsername];

        var isWindowsImage = p[CreateVmHandler.PayloadImage]
            .StartsWith("MicrosoftWindowsServer:", StringComparison.OrdinalIgnoreCase);
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

    private static ResourceGroupResource GetResourceGroup(ArmClient armClient, OperationRequest request)
    {
        if (!ResourceIdentifier.TryParse(request.ResourceId, out var vmId) || vmId is null ||
            string.IsNullOrWhiteSpace(vmId.ResourceGroupName))
        {
            throw new CloudFlow.Core.Errors.OperationValidationException(
                $"无法从请求解析资源组（请求指向：{request.ResourceId}）。");
        }

        return armClient.GetResourceGroupResource(
            new ResourceIdentifier($"/subscriptions/{request.SubscriptionId}/resourceGroups/{vmId.ResourceGroupName}"));
    }

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
