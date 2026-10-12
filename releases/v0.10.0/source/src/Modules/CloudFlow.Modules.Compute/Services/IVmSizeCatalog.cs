namespace CloudFlow.Modules.Compute.Services;

/// <summary>虚拟机规格的关键规格值。</summary>
/// <param name="MemoryMb">内存总量（MB），如 4096。</param>
/// <param name="VCpus">vCPU 核数，如 2。</param>
/// <param name="VCpusPerCore">
/// 每个核心的线程数（SKU 能力 <c>vCPUsPerCore</c>），如 2。
/// null = SKU 未返回该能力，**不等于 1** —— 界面显示 "—" 而不是编一个数字。
/// 注意这个值确实会变：`Standard_B1s` 是 1，`Standard_B2als_v2` 是 2（已实测），不能按规格系列假设。
/// </param>
/// <param name="HyperVGenerations">
/// SKU 能力 <c>HyperVGenerations</c>，如 "V1"、"V1,V2"。null = SKU 未返回该能力。
/// 创建虚拟机选规格时用于提醒"这个规格不支持 Gen2，配 Gen2 镜像会被 Azure 拒绝"——
/// 老规格系列（如 A 系列）常常只支持 V1，配现在市面上大多数默认 Gen2 的新镜像会直接 400。
/// </param>
public readonly record struct VmSizeInfo(
    int MemoryMb, int VCpus, int? VCpusPerCore = null, string? HyperVGenerations = null);

/// <summary>
/// 虚拟机规格目录：按区域提供「规格名 → 规格值」。
///
/// Azure 的 VM 对象本身不含内存与核数（<c>properties.hardwareProfile</c> 只有规格名），
/// 二者只能由规格推导，因此单独抽象。此接口只提供数据访问，不含任何写操作。
/// </summary>
public interface IVmSizeCatalog
{
    /// <summary>
    /// 返回指定订阅 / 区域下所有规格的规格值。
    /// 查询失败时返回空字典并记录警告 —— 内存与 vCPU 列降级为「—」，不影响虚拟机列表本身可用。
    /// </summary>
    Task<IReadOnlyDictionary<string, VmSizeInfo>> GetBySizeAsync(
        string subscriptionId,
        string location,
        CancellationToken ct = default);
}
