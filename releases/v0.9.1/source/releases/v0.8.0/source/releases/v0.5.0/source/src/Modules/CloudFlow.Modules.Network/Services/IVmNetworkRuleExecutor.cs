using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// NSG 规则的写操作执行器（入站与出站同一条路径，方向由 <see cref="NsgRuleDraft.Direction"/> 决定）。
///
/// 契约在 Modules，ARM 实现在 CloudFlow.Azure，App 层按请求携带的 ProviderType 路由。
/// 这是"真实账户下网络写操作只改内存"的根因修复：Handler 过去直接依赖
/// <see cref="MockVmNetworkService"/>，因此无论用哪个账户登录，写操作都落在演示数据上。
/// </summary>
public interface IVmNetworkRuleExecutor
{
    /// <summary>新增允许规则，返回 Azure Request ID（如有）。</summary>
    Task<string?> OpenPortAsync(
        OperationRequest request, string nsgId, NsgRuleDraft draft, CancellationToken ct = default);

    /// <summary>把规则的目标端口改成 newPort，其余字段保持不变（§22）。</summary>
    Task<string?> ChangePortAsync(
        OperationRequest request, string nsgId, string ruleName, int newPort, CancellationToken ct = default);

    /// <summary>删除规则。</summary>
    Task<string?> DeleteRuleAsync(
        OperationRequest request, string nsgId, string ruleName, CancellationToken ct = default);

    /// <summary>
    /// 统计子网内 VM 数（共享 NSG 影响面，§25）。
    /// 统计失败返回 null —— 影响分析降级为只显示 NSG 名，不能因此阻塞操作。
    /// </summary>
    Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default);
}
