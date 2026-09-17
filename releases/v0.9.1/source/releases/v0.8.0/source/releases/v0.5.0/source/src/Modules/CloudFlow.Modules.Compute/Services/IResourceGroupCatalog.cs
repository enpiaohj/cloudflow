namespace CloudFlow.Modules.Compute.Services;

/// <summary>一个资源组及其所在区域。</summary>
/// <param name="Name">资源组名称。</param>
/// <param name="Location">资源组的区域短名称（如 koreacentral）——资源组一旦建好，区域不可变。</param>
public readonly record struct ResourceGroupInfo(string Name, string Location);

/// <summary>
/// 资源组目录：给定订阅，返回该订阅下**所有**已存在的资源组——不是"有 VM 的资源组"，
/// 是这个订阅下不管里面装了什么资源、甚至是空的资源组，全部列出来，并带上各自的实际区域
/// （创建向导用来"选了已有资源组就自动把区域跟着填对"，避免像之前那样选错区域撞上
/// 409 InvalidResourceGroupLocation）。
///
/// 之前创建虚拟机向导的资源组下拉是从"当前已知的 VM 列表"反推的，专门管网络/存储等其它资源、
/// 或者刚建好还没放虚拟机的资源组不会出现在列表里。
///
/// 与 <see cref="IRegionCatalog"/>/<see cref="IVmSizeCatalog"/> 同一类"只读目录"接口：
/// 查询失败时返回空列表并记录警告，不影响调用方其它功能。
/// </summary>
public interface IResourceGroupCatalog
{
    Task<IReadOnlyList<ResourceGroupInfo>> GetAllAsync(string subscriptionId, CancellationToken ct = default);
}
