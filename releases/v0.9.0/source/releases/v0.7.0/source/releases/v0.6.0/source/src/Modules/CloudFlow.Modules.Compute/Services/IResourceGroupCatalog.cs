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
    /// <param name="forceRefresh">
    /// 跳过缓存直接查 ARM——"资源组"/"所有资源"页面的刷新按钮点了就该拿到真数据，
    /// 不能让创建向导那边为了"能立刻看到刚建的资源组"而设的 10 分钟缓存挡在中间
    /// （真实报过的 Bug：删除资源组后点刷新，缓存没到期，列表里那一行还在，
    /// 而 Azure 后台其实已经没有这个资源组了）。默认 <c>false</c>：创建向导等其它调用方
    /// 沿用原来的缓存行为，不因为这个参数而变慢。
    /// </param>
    Task<IReadOnlyList<ResourceGroupInfo>> GetAllAsync(
        string subscriptionId, bool forceRefresh = false, CancellationToken ct = default);
}
