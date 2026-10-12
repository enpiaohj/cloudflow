namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 虚拟机价格估算：给定区域 + 规格 + 操作系统类型，返回预估的月度 USD 费用（近似 Azure Portal
/// 创建向导里"预估费用"那一栏，仅供参考，不含存储/网络/许可证等其它计费项）。
///
/// 与 <see cref="IRegionCatalog"/>/<see cref="IVmSizeCatalog"/> 同一类"只读目录"接口：
/// 查询失败时返回 null，调用方降级为不显示预估费用，不影响创建流程本身。
/// </summary>
public interface IVmPriceCatalog
{
    /// <summary>
    /// 按 730 小时/月折算（Azure 官方定价计算器的月度换算惯例）的预估费用（USD）。
    /// 只覆盖计算费用本身（对应 Pay-as-you-go 消费型价目），不含 Spot/预留实例等其它计费模式。
    /// </summary>
    Task<decimal?> GetMonthlyEstimateUsdAsync(
        string vmSize,
        string region,
        bool isWindows,
        CancellationToken ct = default);
}
