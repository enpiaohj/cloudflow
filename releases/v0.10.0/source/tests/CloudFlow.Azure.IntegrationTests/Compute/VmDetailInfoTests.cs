using CloudFlow.Modules.Compute.Models;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Compute;

/// <summary>
/// 概览页展示文本的语义：**null 必须映射成 null，不能映射成"未启用"/"已禁用"**。
/// 这条是概览页"读不到就不显示该行"的地基 —— 一旦某个格式化方法把 null 兜成确定值，
/// 界面上就再也分不出"Azure 说没有"和"我们没读到"了。
/// </summary>
public sealed class VmDetailInfoTests
{
    [Fact]
    public void 布尔为null时全部返回null而不是确定值()
    {
        var info = new VmDetailInfo();

        Assert.Null(info.HibernationText);
        Assert.Null(info.SecureBootText);
        Assert.Null(info.VTpmText);
        Assert.Null(info.IntegrityMonitoringText);
        Assert.Null(info.AutoShutdownText);
    }

    [Fact]
    public void 布尔false映射为已禁用_但自动关闭用未启用()
    {
        var info = new VmDetailInfo
        {
            HibernationEnabled = false,
            SecureBootEnabled = false,
            VTpmEnabled = false,
            AutoShutdownEnabled = false
        };

        // vm-01 实测：hibernationEnabled=false，门户显示「休眠 已禁用」
        Assert.Equal("已禁用", info.HibernationText);
        Assert.Equal("已禁用", info.SecureBootText);
        Assert.Equal("已禁用", info.VTpmText);
        // 门户对自动关闭的措辞是「未启用」，跟随
        Assert.Equal("未启用", info.AutoShutdownText);
    }

    [Fact]
    public void 布尔true映射为已启用()
    {
        var info = new VmDetailInfo { SecureBootEnabled = true, AutoShutdownEnabled = true };

        Assert.Equal("已启用", info.SecureBootText);
        Assert.Equal("已启用", info.AutoShutdownText);
    }

    [Theory]
    [InlineData("TrustedLaunch", "受信任启动")]
    [InlineData("ConfidentialVM", "机密虚拟机")]
    [InlineData("Standard", "标准")]
    [InlineData("trustedlaunch", "受信任启动")]
    public void 安全类型映射为中文(string raw, string expected) =>
        Assert.Equal(expected, new VmDetailInfo { SecurityType = raw }.SecurityTypeText);

    [Fact]
    public void 未知安全类型原样返回而不是吞掉()
    {
        // 微软以后新增一种安全类型时，宁可界面显示英文原名，也不能显示成空白或"未知"
        Assert.Equal("SomeFutureType",
            new VmDetailInfo { SecurityType = "SomeFutureType" }.SecurityTypeText);
        Assert.Null(new VmDetailInfo { SecurityType = "" }.SecurityTypeText);
    }

    [Fact]
    public void 内存按GiB展示()
    {
        Assert.Equal("4 GiB", new VmDetailInfo { MemoryMb = 4096 }.MemoryText);
        Assert.Equal("1 GiB", new VmDetailInfo { MemoryMb = 1024 }.MemoryText);
        // 非整数 GiB 不四舍五入成整数，避免把 3.5 GiB 显示成 4 GiB
        Assert.Equal("3.5 GiB", new VmDetailInfo { MemoryMb = 3584 }.MemoryText);
        Assert.Null(new VmDetailInfo().MemoryText);
    }

    [Fact]
    public void 创建时间按门户格式展示为UTC()
    {
        var info = new VmDetailInfo
        {
            TimeCreated = new DateTimeOffset(2026, 9, 4, 6, 54, 0, TimeSpan.Zero)
        };

        Assert.Equal("2026/9/4 UTC 06:54", info.TimeCreatedText);
    }

    [Fact]
    public void 非UTC时间先换算到UTC再展示()
    {
        // ARM 理论上都返回 UTC，但不能假设；带偏移的时间必须换算，否则会显示成错误的时刻
        var info = new VmDetailInfo
        {
            TimeCreated = new DateTimeOffset(2026, 9, 4, 15, 54, 0, TimeSpan.FromHours(9))
        };

        Assert.Equal("2026/9/4 UTC 06:54", info.TimeCreatedText);
    }

    [Fact]
    public void 无创建时间时不显示而不是显示纪元时间()
    {
        Assert.Null(new VmDetailInfo().TimeCreatedText);
    }

    [Fact]
    public void 每核线程数为null时保持null_不默认成1或2()
    {
        // Standard_B1s 实测 vCPUsPerCore=1，Standard_B2als_v2 实测=2 —— 确实会变，
        // 所以缺这个能力时必须留空，不能按"多数是 2"去猜
        Assert.Null(new VmDetailInfo().VCpusPerCoreText);
        Assert.Equal("1", new VmDetailInfo { VCpusPerCore = 1 }.VCpusPerCoreText);
        Assert.Equal("2", new VmDetailInfo { VCpusPerCore = 2 }.VCpusPerCoreText);
    }

    [Fact]
    public void 无可用性区域时返回null而不是空字符串()
    {
        Assert.Null(new VmDetailInfo().ZonesText);
        Assert.Equal("1", new VmDetailInfo { Zones = ["1"] }.ZonesText);
        Assert.Equal("1, 2", new VmDetailInfo { Zones = ["1", "2"] }.ZonesText);
    }

    [Fact]
    public void 完整性监视在SDK与ARM都不返回时保持null()
    {
        // Compute SDK 1.16.0 的 UefiSettings 只有 SecureBoot / VTpm；vm-01 实测 ARM 也不返回
        // integrityMonitoringEnabled。所以这一项恒为 null，界面显示 "—" 并说明原因，
        // 不跟着门户把它当成"已禁用"。
        var info = new VmDetailInfo { SecurityType = "TrustedLaunch", SecureBootEnabled = true, VTpmEnabled = false };

        Assert.Null(info.IntegrityMonitoringText);
    }
}
