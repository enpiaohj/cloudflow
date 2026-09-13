using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 创建虚拟机的执行器（设计文档 v3.1 §87）。
/// 契约在 Modules、ARM 实现在 CloudFlow.Azure、App 层按请求携带的 ProviderType 路由 ——
/// 与 <see cref="IVmDeleteExecutor"/> 同构。
/// </summary>
/// <remarks>
/// <b>刻意独立契约，不往只读的 <see cref="IVmInventoryService"/> 上挂写方法</b> ——
/// 与磁盘快照的 <see cref="IVmDiskSnapshotExecutor"/> 同一条纪律：读接口上出现写方法本身就是绕过后门。
/// </remarks>
public interface IVmProvisioningExecutor
{
    /// <summary>
    /// 创建虚拟机（NIC 与公网 IP 由载荷表达，OS 盘随 StorageProfile 隐式创建），
    /// 返回 Azure Request ID（如有）。
    /// </summary>
    /// <param name="resolvePassword">
    /// 管理员密码的<b>解析委托</b>（密码方式时使用）。<b>密码本体刻意不进载荷</b> ——
    /// 载荷会随待审批请求落盘（jobs.json），密码进载荷就是明文落盘。
    /// 载荷只带凭据 Id（<c>credentialId</c>），由 App 层路由器把"凭 Id 从凭据库解密"这个
    /// 能力以委托形式注入：Modules 与 Azure 层都不需要认识凭据库，依赖方向不倒挂；
    /// 明文在执行器内即取即用，不保存、不记日志。
    /// </param>
    Task<string?> CreateAsync(
        OperationRequest request,
        Func<CancellationToken, Task<string?>>? resolvePassword,
        CancellationToken ct = default);

    /// <summary>该请求指向的虚拟机是否已存在且预配成功。Verify 用。</summary>
    Task<bool> VmReadyAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="request"/> 目标资源组是否已存在（资源组名从 <see cref="OperationRequest.ResourceId"/>
    /// 解析，与 <see cref="CreateAsync"/> 内部定位资源组的方式一致）。仅供 Impact 分析读用
    /// （决定要不要在描述里说"将新建资源组"），不影响 <see cref="CreateAsync"/> 的行为——
    /// 那边始终按"不存在则建、存在则复用"的幂等语义执行，不依赖这里的查询结果。
    /// </summary>
    Task<bool> ResourceGroupExistsAsync(OperationRequest request, CancellationToken ct = default);
}
