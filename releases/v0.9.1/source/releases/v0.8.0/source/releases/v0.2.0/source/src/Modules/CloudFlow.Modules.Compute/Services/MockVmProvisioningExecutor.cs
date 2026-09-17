using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 模式的创建执行器：往内存演示数据面<b>真的</b>加一台 VM。
/// </summary>
/// <remarks>
/// 与 <see cref="MockVmDeleteExecutor"/> 对称：演示数据是"真的会变"的 ——
/// 创建后列表里就多一台，删除后就少一台。只在未登录（Demo）时注册。
/// </remarks>
public sealed class MockVmProvisioningExecutor(MockVmInventoryService inventory) : IVmProvisioningExecutor
{
    private const int SimulatedLatencyMs = 900;

    public async Task<string?> CreateAsync(
        OperationRequest request,
        Func<CancellationToken, Task<string?>>? resolvePassword,
        CancellationToken ct = default)
    {
        await Task.Delay(SimulatedLatencyMs, ct).ConfigureAwait(false);

        var p = request.Payload;
        var name = p[CreateVmHandler.PayloadVmName];

        var vm = new VmSummary
        {
            ResourceId = $"{request.ResourceId}",
            Name = name,
            SubscriptionId = request.SubscriptionId,
            ResourceGroupName = ResourceGroupOf(request.ResourceId),
            Region = "koreacentral",
            VmSize = p[CreateVmHandler.PayloadVmSize],
            OsType = VmOsType.Linux,
            OsName = "ubuntu",
            OsVersion = "24.04",
            PrivateIp = $"10.0.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(2, 253)}",
            PublicIp = string.Equals(p.GetValueOrDefault(CreateVmHandler.PayloadPublicIp), "true", StringComparison.OrdinalIgnoreCase)
                ? $"20.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(2, 253)}"
                : null,
            PowerState = VmPowerState.Running,
        };

        inventory.Add(vm);
        return Guid.NewGuid().ToString();
    }

    public Task<bool> VmReadyAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(inventory.FindById(request.ResourceId) is not null);

    /// <summary>Resource ID 中段即资源组名（…/resourceGroups/{rg}/providers/…）。</summary>
    private static string ResourceGroupOf(string resourceId)
    {
        var segments = resourceId.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return "rg-cloudflow-demo";
    }
}
