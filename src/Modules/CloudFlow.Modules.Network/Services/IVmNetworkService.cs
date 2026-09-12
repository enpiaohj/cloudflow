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

    /// <summary>按 RuleId 查找规则。</summary>
    Task<NsgSecurityRule?> FindRuleAsync(string vmResourceId, string ruleId, CancellationToken ct = default);

    /// <summary>读取规则的当前值（Change Port 流程第一步，§22）。</summary>
    Task<IReadOnlyList<NsgSecurityRule>> GetInboundRulesAsync(string vmResourceId, CancellationToken ct = default);
}
