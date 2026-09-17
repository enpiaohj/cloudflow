using System.Globalization;

namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// 虚拟机**详情**（ARM <c>GET .../virtualMachines/{name}</c>）的读取结果。
///
/// 与 <see cref="VmSummary"/> 的分工：列表页的 <c>VmSummary</c> 来自 Azure Resource Graph，
/// 只含清单列需要的字段；详情页要展示的规格能力、安全性、休眠、创建时间等都在 VM 资源本身上，
/// 按项目规则「详情用 ARM API」单独读一次。
///
/// **所有字段可空，null 的含义是「Azure 没有返回这一项」**，界面据此**不渲染该行**，
/// 而不是渲染成 "—" 或编一个默认值。VM GET 是单次请求，要么整体成功要么整体失败
/// （失败时服务抛异常，由上层如实报错），所以"字段为空"确实等于"Azure 里就没有"，
/// 不会把读取失败伪装成"没有"。
/// </summary>
public sealed record VmDetailInfo
{
    // ---- 大小 / 计算 ----

    /// <summary>规格名，如 Standard_B2als_v2。来自 <c>hardwareProfile.vmSize</c>。</summary>
    public string? VmSize { get; init; }

    /// <summary>vCPU 核数。VM 资源本身不含，由规格目录（SKU capabilities）推导。</summary>
    public int? VCpus { get; init; }

    /// <summary>内存（MB）。同上，由规格目录推导。</summary>
    public int? MemoryMb { get; init; }

    /// <summary>每个核心的线程数（SKU 能力 <c>vCPUsPerCore</c>）。null = SKU 未返回该能力。</summary>
    public int? VCpusPerCore { get; init; }

    public string? ProvisioningState { get; init; }

    public DateTimeOffset? TimeCreated { get; init; }

    /// <summary><c>additionalCapabilities.hibernationEnabled</c>。null = 该 VM 未返回此属性。</summary>
    public bool? HibernationEnabled { get; init; }

    // ---- 操作系统 ----

    public string? OsType { get; init; }

    public string? ComputerName { get; init; }

    public string? AdminUsername { get; init; }

    // ---- 安全性 ----

    /// <summary>原始 securityType：TrustedLaunch / ConfidentialVM / Standard。</summary>
    public string? SecurityType { get; init; }

    public bool? SecureBootEnabled { get; init; }

    public bool? VTpmEnabled { get; init; }

    /// <summary>
    /// 完整性监视。**Compute SDK 与 ARM 当前都不返回这个字段**（实测：`appscloud` 的
    /// uefiSettings 只有 secureBootEnabled 与 vTpmEnabled），因此这里恒为 null，
    /// 界面显示 "—"。门户把它显示成"已禁用"是把缺失当 false —— 本应用不跟着编。
    /// </summary>
    public bool? IntegrityMonitoringEnabled { get; init; }

    // ---- 电源计划 ----

    /// <summary>是否配置了自动关闭计划（DevTestLab schedule）。null = 未查询到该资源。</summary>
    public bool? AutoShutdownEnabled { get; init; }

    /// <summary>已计划的关闭时间，如 "19:00 (Korea Standard Time)"。无计划时为 null。</summary>
    public string? ScheduledShutdownText { get; init; }

    /// <summary>实例 ID（<c>properties.vmId</c>）。与 ARM Resource ID 不是一回事。</summary>
    public string? VmId { get; init; }

    // ---- 位置与可用性 ----

    /// <summary>可用性区域，如 ["1"]。非区域性 VM 为空。</summary>
    public IReadOnlyList<string> Zones { get; init; } = [];

    /// <summary>可用性集名称。未加入可用性集时为 null。</summary>
    public string? AvailabilitySetName { get; init; }

    // ---- 展示文本（格式化集中在这里，可被单测直接断言）----

    /// <summary>内存展示文本，与门户一致（"4 GiB"）。</summary>
    public string? MemoryText => MemoryMb is { } mb ? $"{mb / 1024.0:0.##} GiB" : null;

    public string? VCpusText => VCpus?.ToString(CultureInfo.InvariantCulture);

    public string? VCpusPerCoreText => VCpusPerCore?.ToString(CultureInfo.InvariantCulture);

    /// <summary>创建时间，与门户一致（"2026/9/4 UTC 06:54"）。</summary>
    public string? TimeCreatedText => TimeCreated is { } t
        ? t.ToUniversalTime().ToString("yyyy/M/d 'UTC' HH:mm", CultureInfo.InvariantCulture)
        : null;

    /// <summary>安全性类型的中文名。未知值原样返回，不吞掉。</summary>
    public string? SecurityTypeText => SecurityType switch
    {
        null or "" => null,
        var s when s.Equals("TrustedLaunch", StringComparison.OrdinalIgnoreCase) => "受信任启动",
        var s when s.Equals("ConfidentialVM", StringComparison.OrdinalIgnoreCase) => "机密虚拟机",
        var s when s.Equals("Standard", StringComparison.OrdinalIgnoreCase) => "标准",
        var s => s
    };

    public string? SecureBootText => FormatSwitch(SecureBootEnabled);

    public string? VTpmText => FormatSwitch(VTpmEnabled);

    public string? IntegrityMonitoringText => FormatSwitch(IntegrityMonitoringEnabled);

    public string? HibernationText => FormatSwitch(HibernationEnabled);

    /// <summary>
    /// 自动关闭。已配置计划 → "已启用"；确认没有计划 → "未启用"（沿用门户措辞）；
    /// null（没查到）→ null，该行不显示。
    /// </summary>
    public string? AutoShutdownText => AutoShutdownEnabled switch
    {
        true => "已启用",
        false => "未启用",
        _ => null
    };

    /// <summary>可用性区域，如 "1"；多个区域用逗号连接。无区域时为 null。</summary>
    public string? ZonesText => Zones.Count == 0 ? null : string.Join(", ", Zones);

    /// <summary>
    /// 布尔 → 中文。**null 一律映射为 null**，而不是"未启用"/"已禁用" ——
    /// "没读到"和"读到了并且是 false"是两件事，混起来就会把未知说成确定。
    /// </summary>
    private static string? FormatSwitch(bool? value) => value switch
    {
        true => "已启用",
        false => "已禁用",
        _ => null
    };
}
