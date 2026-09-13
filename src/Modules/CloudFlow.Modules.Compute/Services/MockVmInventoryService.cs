using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Mock VM Inventory（Demo 模式）。
/// 数据与 UI 概念图 1 一致：42 台 Production VM（前 10 台名称/规格/区域与概念图对应），
/// 另有 8 台 Development VM 用于演示多订阅过滤。
/// 接入真实 Azure 后替换为 Resource Graph 实现。
/// </summary>
public sealed class MockVmInventoryService : IVmInventoryService
{
    public const string SubProdChina = "11111111-1111-1111-1111-111111111111";
    public const string SubProdKorea = "22222222-2222-2222-2222-222222222222";
    public const string SubProdUs = "33333333-3333-3333-3333-333333333333";
    public const string SubDev = "44444444-4444-4444-4444-444444444444";

    private readonly List<VmSummary> _vms;

    public MockVmInventoryService()
    {
        _vms = BuildDemoVms();
    }

    public Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default)
    {
        var result = _vms
            .Where(vm => scope.ContainsSubscription(vm.SubscriptionId))
            .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<VmSummary>>(result);
    }

    /// <summary>按 Resource ID 查找（供 Operation Handler 校验使用）。</summary>
    public VmSummary? FindById(string resourceId) =>
        _vms.FirstOrDefault(vm => string.Equals(vm.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从演示数据面移除一台 VM（删除操作执行器用）。
    /// 返回是否真的移除了 —— 演示数据面是"真的会变"的，删掉之后列表里就不该再有它。
    /// </summary>
    public bool Remove(string resourceId) =>
        _vms.RemoveAll(vm => string.Equals(vm.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase)) > 0;

    private static List<VmSummary> BuildDemoVms()
    {
        List<VmSummary> vms =
        [
            // —— 与概念图 1 完全对应的前 10 台 ——
            Vm("WEB01", SubProdChina, "rg-web", "East US", "Standard_B2ms", VmPowerState.Running, "10.0.1.4", cpu: 12),
            Vm("SQL01", SubProdChina, "rg-data", "East US", "Standard_D4s_v5", VmPowerState.Running, "10.0.1.5", cpu: 28),
            Vm("DEV01", SubProdChina, "rg-dev", "West US", "Standard_B2s", VmPowerState.Stopped, warning: true),
            Vm("APP01", SubProdChina, "rg-app", "East US 2", "Standard_B4ms", VmPowerState.Running, "10.0.2.8", cpu: 18),
            Vm("TEST01", SubProdKorea, "rg-test", "West US 2", "Standard_B2s", VmPowerState.Deallocated),
            Vm("BASTION01", SubProdKorea, "rg-shared", "East US", "Standard_B2ms", VmPowerState.Running, "10.0.0.10", cpu: 6),
            Vm("MON01", SubProdKorea, "rg-ops", "Central US", "Standard_B2s", VmPowerState.Running, "10.0.1.20", cpu: 14),
            Vm("OLD-SQL", SubProdKorea, "rg-legacy", "East US", "Standard_D2s_v3", VmPowerState.Running, "10.0.3.15", cpu: 72, warning: true),
            Vm("ANALYTICS01", SubProdUs, "rg-analytics", "West Europe", "Standard_E4s_v5", VmPowerState.Running, "10.0.4.7", cpu: 22),
            Vm("JUMPBOX", SubProdUs, "rg-shared", "East US", "Standard_B2s", VmPowerState.Running, "10.0.0.12", cpu: 8)
        ];

        // —— 其余 32 台 Production（凑足概念图的 42 台）——
        var sizes = new[] { "Standard_B2s", "Standard_B2ms", "Standard_D2s_v5", "Standard_D4s_v5", "Standard_B4ms" };
        var regions = new[] { "East US", "West US", "East US 2", "Central US", "West Europe", "Korea Central" };
        for (var i = 1; i <= 32; i++)
        {
            var running = i % 7 != 0;
            var state = running ? VmPowerState.Running : (i % 2 == 0 ? VmPowerState.Deallocated : VmPowerState.Stopped);
            vms.Add(Vm(
                $"APP-{i:D2}",
                i % 3 == 0 ? SubProdUs : (i % 3 == 1 ? SubProdChina : SubProdKorea),
                i % 2 == 0 ? "rg-app" : "rg-web",
                regions[i % regions.Length],
                sizes[i % sizes.Length],
                state,
                state == VmPowerState.Running ? $"10.1.{i / 250}.{i % 250 + 2}" : null,
                cpu: running ? Random.Shared.Next(3, 65) : null));
        }

        // —— 8 台 Development（验证 Scope 过滤与多订阅视图）——
        for (var i = 1; i <= 8; i++)
        {
            vms.Add(Vm(
                $"DEV-VM{i:D2}",
                SubDev,
                "rg-dev",
                i % 2 == 0 ? "West US" : "East US",
                "Standard_B2s",
                i % 3 == 0 ? VmPowerState.Deallocated : VmPowerState.Running,
                i % 3 == 0 ? null : $"10.8.0.{i + 10}",
                cpu: i % 3 == 0 ? null : Random.Shared.Next(2, 20),
                subscriptionName: "Development"));
        }

        return vms;

        static VmSummary Vm(
            string name, string subId, string rg, string region, string size,
            VmPowerState state, string? publicIp = null, double? cpu = null,
            bool warning = false, string subscriptionName = "Production")
        {
            var isWindows = !name.Contains("SQL", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SQL", StringComparison.OrdinalIgnoreCase);
            return new VmSummary
            {
                ResourceId = $"/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Compute/virtualMachines/{name}",
                Name = name,
                ComputerName = $"{name.ToLowerInvariant()}.{(isWindows ? "contoso.com" : "internal.cloudapp.net")}",
                SubscriptionId = subId,
                SubscriptionName = subscriptionName,
                ResourceGroupName = rg,
                Region = region,
                VmSize = size,
                OsType = name.Contains("SQL", StringComparison.OrdinalIgnoreCase) || name.Contains("ANALYTICS", StringComparison.OrdinalIgnoreCase)
                    ? VmOsType.Linux
                    : VmOsType.Windows,
                OsName = isWindows ? "Windows" : "ubuntu",
                OsVersion = isWindows ? "2022 Datacenter" : "24.04",
                OsImageOffer = isWindows ? "windows-2022" : "ubuntu-24_04-lts",
                PowerState = state,
                HasWarning = warning,
                PublicIp = publicIp,
                PrivateIp = publicIp is null ? null : $"10.0.{name.Length % 5}.5",
                CpuPercent = cpu,
                MemoryMb = MockMemoryMb(size),
                VCpuCount = MockVCpus(size)
            };
        }

        // 演示数据的内存与核数取真实 Azure 规格值（来自 Microsoft.Compute vmSizes 实测），
        // 这样 Demo 模式下「内存」「vCPU」列与真实账户下的读数口径一致。
        static int? MockMemoryMb(string size) => size switch
        {
            "Standard_B2s" => 4096,
            "Standard_B2ms" => 8192,
            "Standard_B4ms" => 16384,
            "Standard_D2s_v3" => 8192,
            "Standard_D4s_v5" => 16384,
            "Standard_E4s_v5" => 32768,
            _ => null
        };

        static int? MockVCpus(string size) => size switch
        {
            "Standard_B2s" => 2,
            "Standard_B2ms" => 2,
            "Standard_B4ms" => 4,
            "Standard_D2s_v3" => 2,
            "Standard_D4s_v5" => 4,
            "Standard_E4s_v5" => 4,
            _ => null
        };
    }
}
