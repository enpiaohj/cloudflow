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
/// 规则方向（ARM 的 <c>SecurityRuleDirection</c>）。
/// 入站规则管"外部 → 本机"，出站规则管"本机 → 外部"。
/// 方向决定用户在对话框里填的那个对端地址落在哪一侧（入站填来源、出站填目标），
/// 所以它是规则的一等属性，不是一个仅供显示的字符串。
/// </summary>
public enum NsgRuleDirection
{
    Inbound,

    Outbound
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

    /// <summary>
    /// 来源显示文本，如 "My IP (203.0.113.10)" / "Any" / "10.0.2.0/24" / "AzureLoadBalancer"。
    /// 入站规则上这是用户填的对端；出站规则上它是本机（恒为 "Any"），**不是**对端。
    /// </summary>
    public required string Source { get; init; }

    /// <summary>规则的源地址前缀（原始值，供 Change Port / Impact 判定）。</summary>
    public string SourcePrefix { get; init; } = "*";

    /// <summary>
    /// 目标显示文本。出站规则上这是用户填的对端；入站规则上它是 Azure 的默认目标 ("Any")。
    /// 与 <see cref="Source"/> 成对出现，界面按方向取用其一，不需要调用方自己判断该看哪一侧。
    /// </summary>
    public string Destination { get; init; } = "Any";

    /// <summary>规则的目标地址前缀（原始值）。入站规则恒为 "*"。</summary>
    public string DestinationPrefix { get; init; } = "*";

    /// <summary>
    /// 目标端口。入站规则上是本机被访问的端口；出站规则上是本机要访问的对端端口
    /// （ARM 两侧都用 <c>destinationPortRange</c>，语义随方向翻转）。
    /// </summary>
    public required int DestinationPort { get; set; }

    public required NsgProtocol Protocol { get; init; }

    public required NsgRuleAction Action { get; init; }

    public required NsgRuleOrigin Origin { get; init; }

    public required int Priority { get; set; }

    /// <summary>规则方向。默认入站 —— 既有调用方不写这一项时行为不变。</summary>
    public NsgRuleDirection Direction { get; init; } = NsgRuleDirection.Inbound;
}
