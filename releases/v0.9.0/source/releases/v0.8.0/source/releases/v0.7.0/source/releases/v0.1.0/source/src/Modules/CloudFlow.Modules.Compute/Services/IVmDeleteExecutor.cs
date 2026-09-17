using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// VM 删除操作的执行器（设计文档 v3.1 §87）。
///
/// 契约在 Modules、ARM 实现在 CloudFlow.Azure、App 层按请求携带的 ProviderType 路由 ——
/// 与 <see cref="IVmPowerExecutor"/> 同构，因此同一份 Handler 既跑 Demo 内存数据面，也打真实 ARM。
/// </summary>
/// <remarks>
/// <b>刻意独立于 <see cref="IVmPowerExecutor"/>，不往上加方法。</b>
/// 删除的 Verify 语义是「资源已消失」（404 判定），与电源操作的「状态等于某值」是两套语义；
/// 塞进同一个接口会让它同时承担两者，而两边的实现方（Mock / ARM）本来也没有共同代码可复用。
/// </remarks>
public interface IVmDeleteExecutor
{
    /// <summary>
    /// 读回该 VM <b>当前真实附着</b>的可连带删除资源。
    /// </summary>
    /// <remarks>
    /// 影响分析据此算数量与文案，执行阶段据此与请求求交集 —— <b>两处都必须以这里的结果为准</b>。
    /// </remarks>
    Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
        OperationRequest request, CancellationToken ct = default);

    /// <summary>
    /// 删除虚拟机本身，返回 Azure Request ID（如有）。
    /// </summary>
    /// <remarks>
    /// <b>调用顺序由 Handler 决定，不由本方法决定</b>：必须是「先删 VM、再删连带资源」——
    /// NIC 与磁盘<b>附着在 VM 上时无法删除</b>，反过来会拿到 <c>OperationNotAllowed</c>。
    /// 顺序放在 Handler 里，是因为它是一条<b>可断言的不变量</b>：
    /// 藏在执行器内部就只能靠真机验证，而它在两个 Provider 实现里都得成立。
    /// </remarks>
    Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default);

    /// <summary>
    /// 删除一件连带资源。返回 <c>false</c> 表示它<b>仍然存在</b>（删除失败）；<c>404</c> 视为成功。
    /// </summary>
    /// <remarks>
    /// 返回布尔而不是抛异常：失败要由 Handler 汇总成一句「虚拟机已删，但这些还在」，
    /// 而抛出 Azure 的异常类型会把 Provider 细节漏进本该与 Provider 无关的 Handler。
    /// </remarks>
    Task<bool> DeleteLinkedAsync(
        OperationRequest request, VmLinkedResource resource, CancellationToken ct = default);

    /// <summary>
    /// 该请求指向的<b>虚拟机</b>是否还存在（404 视为不存在）。
    /// Execute 的前置校验与 Verify 都用它 —— 只针对 VM 本身，连带资源的存亡由
    /// <see cref="GetLinkedResourcesAsync"/> 的结果体现。
    /// </summary>
    Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default);
}
