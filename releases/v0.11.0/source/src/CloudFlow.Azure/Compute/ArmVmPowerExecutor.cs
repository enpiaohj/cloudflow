using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 真实 Azure 电源 / 规格执行器：CLI 不参与资源操作，链路为
/// OperationEngine → VmPowerHandlers → 本执行器 → IAzureClientFactory → ArmClient → ARM LRO。
///
/// 目标资源完全由 request.ResourceId 解析（Azure Resource ID 是唯一主键），不从 Payload 取名称 ——
/// 名称与 ID 不一致是历史 Bug 的来源。
/// </summary>
public sealed class ArmVmPowerExecutor(
    IAzureClientFactory clientFactory,
    ILogger<ArmVmPowerExecutor> logger) : IVmPowerExecutor
{
    public async Task<VmPowerState?> GetPowerStateAsync(OperationRequest request, CancellationToken ct = default)
    {
        var vm = await GetVmAsync(request, ct).ConfigureAwait(false);
        var instanceView = (await vm.InstanceViewAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
        return MapPowerState(instanceView);
    }

    public async Task<string?> GetVmSizeAsync(OperationRequest request, CancellationToken ct = default)
    {
        var vm = await GetVmAsync(request, ct).ConfigureAwait(false);
        var refreshed = (await vm.GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
        return DescribeSize(refreshed.Data.HardwareProfile);
    }

    public async Task<string?> ExecuteAsync(
        OperationRequest request, VmPowerAction action, CancellationToken ct = default)
    {
        var vm = await GetVmAsync(request, ct).ConfigureAwait(false);

        ArmOperation operation = action switch
        {
            VmPowerAction.Start => await vm.PowerOnAsync(WaitUntil.Completed, ct).ConfigureAwait(false),
            VmPowerAction.Restart => await vm.RestartAsync(WaitUntil.Completed, ct).ConfigureAwait(false),
            VmPowerAction.PowerOff => await vm.PowerOffAsync(WaitUntil.Completed, cancellationToken: ct).ConfigureAwait(false),
            VmPowerAction.Deallocate => await vm.DeallocateAsync(WaitUntil.Completed, cancellationToken: ct).ConfigureAwait(false),
            _ => throw new CloudFlowException(CloudFlowErrorCode.NotSupported, $"不支持的电源动作：{action}")
        };

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM power {Action} on {ResourceId} (requestId {RequestId})",
            action, request.ResourceId, requestId);
        return requestId;
    }

    public async Task<string?> ResizeAsync(
        OperationRequest request, string newSize, CancellationToken ct = default)
    {
        var vm = await GetVmAsync(request, ct).ConfigureAwait(false);

        // 规格变更走 PATCH：ARM 的 UpdateAsync 只接受 VirtualMachinePatch，
        // 且必须 PATCH 语义 —— 用 PUT 会把未读取到的字段当成"要清空"。
        var patch = new VirtualMachinePatch
        {
            HardwareProfile = new VirtualMachineHardwareProfile { VmSize = newSize }
        };

        var operation = await vm.UpdateAsync(WaitUntil.Completed, patch, ct).ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM resize {ResourceId} → {Size} (requestId {RequestId})",
            request.ResourceId, newSize, requestId);
        return requestId;
    }

    /// <summary>按请求认证上下文取 ARM Client，并解析请求指向的虚拟机资源。</summary>
    private async Task<VirtualMachineResource> GetVmAsync(OperationRequest request, CancellationToken ct)
    {
        // 解析与类型校验走 ArmVmTarget 的同一份实现 —— 删除执行器也要做同样的事，
        // 两处各写一遍的话，漏改一个就会让某个操作绕过类型校验。
        var resourceId = ArmVmTarget.ParseVmId(request);

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        return armClient.GetVirtualMachineResource(resourceId);
    }

    /// <summary>
    /// InstanceView 的 PowerState 只有"正在运行/正在关机"这类瞬时值，且不同平台会给出
    /// PowerState/starting、PowerState/stopping 等中间态 —— 未识别的取值返回 null 而不是猜。
    /// </summary>
    private static VmPowerState? MapPowerState(VirtualMachineInstanceView view)
    {
        var code = view.Statuses
            .Select(status => status.Code)
            .FirstOrDefault(c => c is not null && c.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase));

        return code?.ToLowerInvariant() switch
        {
            "powerstate/running" or "powerstate/starting" or "powerstate/restarting" => VmPowerState.Running,
            "powerstate/stopped" or "powerstate/stopping" => VmPowerState.Stopped,
            "powerstate/deallocated" or "powerstate/deallocating" => VmPowerState.Deallocated,
            _ => null
        };
    }

    private static string? DescribeSize(VirtualMachineHardwareProfile? profile) =>
        profile?.VmSize?.ToString();

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
