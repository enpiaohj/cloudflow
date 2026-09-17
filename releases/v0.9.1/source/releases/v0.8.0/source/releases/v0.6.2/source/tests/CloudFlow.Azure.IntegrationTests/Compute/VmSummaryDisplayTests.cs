using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Compute;

/// <summary>
/// 列表新增的「操作系统」「内存」两列的显示逻辑。
/// 内存只能由规格推导（Azure VM 对象不含内存字段），推导不到时必须显示 "—" 而不是 0 或空白。
/// </summary>
public sealed class VmSummaryDisplayTests
{
    [Theory]
    [InlineData(VmOsType.Linux, "Linux")]
    [InlineData(VmOsType.Windows, "Windows")]
    public void OsTypeText_映射为Azure门户口径(VmOsType osType, string expected)
    {
        var vm = MakeVm(osType: osType);

        Assert.Equal(expected, vm.OsTypeText);
    }

    [Theory]
    [InlineData(4096, "4 GiB")]
    [InlineData(8192, "8 GiB")]
    [InlineData(32768, "32 GiB")]
    [InlineData(512, "0.5 GiB")]   // 不足 1 GiB 时不能让整除分支吃掉小数
    [InlineData(1536, "1.5 GiB")]
    public void MemoryText_按GiB格式化(int memoryMb, string expected)
    {
        var vm = MakeVm(memoryMb: memoryMb);

        Assert.Equal(expected, vm.MemoryText);
    }

    [Fact]
    public void MemoryText_规格未知时显示破折号而不是0()
    {
        var vm = MakeVm(memoryMb: null);

        Assert.Equal("—", vm.MemoryText);
    }

    [Theory]
    [InlineData(2, "2")]
    [InlineData(16, "16")]
    public void VCpuText_显示核数(int vCpus, string expected)
    {
        var vm = MakeVm(vCpus: vCpus);

        Assert.Equal(expected, vm.VCpuText);
    }

    [Fact]
    public void VCpuText_规格未知时显示破折号而不是0()
    {
        var vm = MakeVm(vCpus: null);

        Assert.Equal("—", vm.VCpuText);
    }

    [Fact]
    public async Task Demo清单_已知规格都带内存且与真实Azure规格值一致()
    {
        var service = new MockVmInventoryService();
        // 默认 Scope 即 AllAccessible，覆盖演示清单里的全部订阅
        var vms = await service.QueryAsync(new ResourceScope());

        // Standard_B2s = 4 GiB、Standard_E4s_v5 = 32 GiB（取自 Microsoft.Compute vmSizes 实测）
        var b2s = vms.First(vm => vm.VmSize == "Standard_B2s");
        var e4s = vms.First(vm => vm.VmSize == "Standard_E4s_v5");

        Assert.Equal("4 GiB", b2s.MemoryText);
        Assert.Equal("32 GiB", e4s.MemoryText);
        Assert.Equal("2", b2s.VCpuText);
        Assert.Equal("4", e4s.VCpuText);
    }

    private static VmSummary MakeVm(
        VmOsType osType = VmOsType.Windows, int? memoryMb = null, int? vCpus = null) => new()
    {
        ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm",
        Name = "vm",
        SubscriptionId = "s",
        OsType = osType,
        MemoryMb = memoryMb,
        VCpuCount = vCpus
    };
}
