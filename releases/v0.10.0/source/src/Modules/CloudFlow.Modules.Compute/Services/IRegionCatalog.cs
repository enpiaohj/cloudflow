namespace CloudFlow.Modules.Compute.Services;

/// <summary>一个 Azure 区域。</summary>
/// <param name="Name">短名称（ARM location，如 koreacentral），提交时使用这个。</param>
/// <param name="DisplayName">展示名（如 Korea Central），下拉里给人看。</param>
public readonly record struct RegionInfo(string Name, string DisplayName);

/// <summary>
/// 区域目录：给定订阅，返回该订阅下真实可用的 Azure 区域列表。
///
/// 与 <see cref="IVmSizeCatalog"/> 同一类"只读目录"接口：不含任何写操作，
/// 查询失败时返回空列表并记录警告，不影响调用方其它功能。
/// </summary>
public interface IRegionCatalog
{
    Task<IReadOnlyList<RegionInfo>> GetRegionsAsync(string subscriptionId, CancellationToken ct = default);
}
