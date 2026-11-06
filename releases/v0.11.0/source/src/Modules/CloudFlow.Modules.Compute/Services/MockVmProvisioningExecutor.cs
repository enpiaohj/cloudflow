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

    /// <summary>
    /// 演示数据里"已存在"的资源组集合，与 <see cref="MockVmInventoryService"/> 的种子数据同名，
    /// 保持 Demo 模式下"这些是老资源组、新名字才会走新建提示"的一致体验。
    /// 创建成功后把这次用到的资源组名也加进去——本次新建的，下次视为已存在。
    /// </summary>
    private readonly HashSet<string> _knownResourceGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "rg-web", "rg-data", "rg-dev", "rg-app", "rg-test",
        "rg-shared", "rg-ops", "rg-legacy", "rg-analytics"
    };

    public async Task<string?> CreateAsync(
        OperationRequest request,
        Func<CancellationToken, Task<string?>>? resolvePassword,
        Func<string, CancellationToken, Task> reportProgress,
        CancellationToken ct = default)
    {
        var p = request.Payload;
        var name = p[CreateVmHandler.PayloadVmName];

        // 演示数据面没有真实的分步 ARM 调用，把一次延时拆成几段、配几句和真实执行器同名的
        // 进度文案——Demo 模式下也能看到"资源组就绪→网络就绪→创建中"这套 UI 效果。
        var step = SimulatedLatencyMs / 3;
        await Task.Delay(step, ct).ConfigureAwait(false);
        await reportProgress("资源组已就绪", ct).ConfigureAwait(false);

        await Task.Delay(step, ct).ConfigureAwait(false);
        await reportProgress("虚拟网络/子网已就绪", ct).ConfigureAwait(false);

        await Task.Delay(SimulatedLatencyMs - 2 * step, ct).ConfigureAwait(false);
        await reportProgress("网卡已创建，正在创建虚拟机…", ct).ConfigureAwait(false);

        // 与真实执行器同一语义：不存在则视为"这次新建"，存在则复用——Demo 模式下没有真实
        // ARM 调用，这里只需要让这个资源组名此后"看起来"已存在即可。
        _knownResourceGroups.Add(ResourceGroupOf(request.ResourceId));

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

    public Task<bool> ResourceGroupExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        Task.FromResult(_knownResourceGroups.Contains(ResourceGroupOf(request.ResourceId)));

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
