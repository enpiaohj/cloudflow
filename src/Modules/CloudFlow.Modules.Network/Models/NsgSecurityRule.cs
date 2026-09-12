namespace CloudFlow.Modules.Network.Models;

public enum NsgRuleAction
{
    Allow,

    Deny
}

/// <summary>规则挂在哪个 NSG 上（概念图中的 Origin 列：NIC / Subnet）。</summary>
public enum NsgRuleOrigin
{
    Nic,

    Subnet
}

public enum NsgProtocol
{
    TCP,

    UDP,

    Any
}

/// <summary>
/// 安全规则视图（Port Manager，设计文档 §21）。
/// 聚合 NIC NSG 与 Subnet NSG 后按优先级展示，用户不需要自己找 NSG（§20）。
/// </summary>
public sealed class NsgSecurityRule
{
    /// <summary>规则在 Azure 侧的完整 Resource ID。</summary>
    public required string RuleId { get; init; }

    public required string Name { get; init; }

    /// <summary>来源显示文本，如 "My IP (203.0.113.10)" / "Any" / "10.0.2.0/24" / "AzureLoadBalancer"。</summary>
    public required string Source { get; init; }

    /// <summary>规则的源地址前缀（原始值，供 Change Port / Impact 判定）。</summary>
    public string SourcePrefix { get; init; } = "*";

    public required int DestinationPort { get; set; }

    public required NsgProtocol Protocol { get; init; }

    public required NsgRuleAction Action { get; init; }

    public required NsgRuleOrigin Origin { get; init; }

    public required int Priority { get; set; }

    public string Direction { get; init; } = "Inbound";
}
