using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Mock 虚拟机详情（Demo 模式）。
///
/// 刻意造出**两种形态**，否则概览页在 Demo 下只能看到"全都有值"这一种渲染路径：
/// - 名字里带 <c>WEB</c> 的（概念图里的 WEB01）：受信任启动 + 安全启动开 + 有可用性区域，
///   完整性监视与自动关闭**没有值** → 这两行应当不显示。
/// - 其余：标准安全性，没有 uefiSettings、没有可用性区域、没有创建时间 → 这些行也不显示。
///
/// 也就是说 Mock 数据本身就覆盖了"字段缺失时行不渲染"这条规则，不必真连 Azure 才能看到。
/// </summary>
public sealed class MockVmDetailService : IVmDetailService
{
    private static readonly DateTimeOffset Web01Created = new(2026, 9, 4, 6, 54, 0, TimeSpan.Zero);

    public Task<VmDetailInfo?> GetAsync(string vmResourceId, CancellationToken ct = default)
    {
        var name = vmResourceId.Split('/').LastOrDefault() ?? "vm";

        return Task.FromResult<VmDetailInfo?>(name.Contains("WEB", StringComparison.OrdinalIgnoreCase)
            ? new VmDetailInfo
            {
                VmSize = "Standard_B2als_v2",
                VCpus = 2,
                MemoryMb = 4096,
                VCpusPerCore = 2,
                ProvisioningState = "Succeeded",
                TimeCreated = Web01Created,
                HibernationEnabled = false,
                OsType = "Linux",
                ComputerName = name.ToLowerInvariant(),
                AdminUsername = "azureuser",
                SecurityType = "TrustedLaunch",
                SecureBootEnabled = true,
                VTpmEnabled = true,
                // 完整性监视保持 null：Demo 数据也不假装 ARM 会返回它
                IntegrityMonitoringEnabled = null,
                AutoShutdownEnabled = true,
                ScheduledShutdownText = "19:00 (Korea Standard Time)",
                Zones = ["1"],
                AvailabilitySetName = null,
            }
            : new VmDetailInfo
            {
                VmSize = "Standard_B1s",
                VCpus = 1,
                MemoryMb = 1024,
                VCpusPerCore = 1,
                ProvisioningState = "Succeeded",
                HibernationEnabled = false,
                OsType = "Windows",
                ComputerName = name.ToLowerInvariant(),
                SecurityType = "Standard",
                SecureBootEnabled = false,
                VTpmEnabled = false,
                AutoShutdownEnabled = false,
                // 没有可用性区域 / 可用性集 / 创建时间 → 对应行不显示
            });
    }
}
