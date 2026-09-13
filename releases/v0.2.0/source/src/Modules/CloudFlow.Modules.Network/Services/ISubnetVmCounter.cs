using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 统计子网内的虚拟机数量（设计文档 §25 共享 NSG 影响分析）。
///
/// 这是"改一条规则会影响多少台机器"的唯一数据来源。查询失败必须返回 null 而不是 0 ——
/// 0 会被读成"没有别的 VM 受影响"，那是拿未知冒充安全。
/// </summary>
public interface ISubnetVmCounter
{
    Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default);
}
