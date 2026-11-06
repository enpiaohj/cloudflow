using CloudFlow.Modules.Network.Models;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// VM 网络上下文查询 + 规则读写接口（Mock 实现 / 后续 ARM 实现）。
/// 规则修改操作仍必须经 Operation Engine（§29），此接口只提供数据访问。
/// </summary>
public interface IVmNetworkService
{
    /// <summary>获取 VM 的聚合网络上下文；无 NIC 时返回 null。</summary>
    Task<VmNetworkContext?> GetForVmAsync(string vmResourceId, CancellationToken ct = default);

    /// <summary>
    /// 单独取 VM 的出站连通性。与 <see cref="GetForVmAsync"/> 走同一条资源图遍历
    /// （实现上就是它的 <see cref="VmNetworkContext.Outbound"/> 部分），
    /// 放在同一接口而不是拆成新服务：入站链路与本解析共享 VM / NIC / 子网三次读取，拆开就是重复请求。
    /// 返回 null 只在"该 VM 没有网卡 / 读取整体失败"时出现；**"没有公网出口"是
    /// <see cref="OutboundConnectivityType.None"/>，不是 null**。
    /// </summary>
    Task<VmOutboundOverview?> GetOutboundAsync(string vmResourceId, CancellationToken ct = default);

    /// <summary>按 RuleId 查找规则。</summary>
    Task<NsgSecurityRule?> FindRuleAsync(string vmResourceId, string ruleId, CancellationToken ct = default);

    /// <summary>读取入站规则的当前值（Change Port 流程第一步，§22）。</summary>
    Task<IReadOnlyList<NsgSecurityRule>> GetInboundRulesAsync(string vmResourceId, CancellationToken ct = default);

    /// <summary>
    /// 读取出站规则的当前值。
    /// 与 <see cref="GetInboundRulesAsync"/> 是同一份 NSG 规则集合的两个方向切片，
    /// 所以**含出站规则时才分开调**：读的是同一次 NSG 读取的结果，不额外访问 Azure。
    /// </summary>
    Task<IReadOnlyList<NsgSecurityRule>> GetOutboundRulesAsync(string vmResourceId, CancellationToken ct = default);
}
