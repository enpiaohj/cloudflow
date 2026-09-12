namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// VM 网络上下文（设计文档 §20 / 概念图 2 左侧 Network Summary + Inbound Rules）。
/// 以 VM 为中心聚合 NIC / VNet / Subnet / NSG，用户不需要跨 Resource Provider 查找。
/// </summary>
public sealed record VmNetworkContext
{
    public required string VmResourceId { get; init; }

    public required string NicName { get; init; }

    public required string VnetName { get; init; }

    public required string SubnetName { get; init; }

    public required string SubnetCidr { get; init; }

    public required string NsgName { get; init; }

    /// <summary>true = 规则位于 Subnet 级 NSG，可能影响同子网多台 VM（§25 Shared NSG）。</summary>
    public bool IsSharedSubnetNsg { get; init; }

    /// <summary>安全状态汇总（如 "Protected"）。</summary>
    public string SecurityState { get; init; } = "Protected";

    public required IReadOnlyList<NsgSecurityRule> InboundRules { get; init; }
}
