namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 待创建的 NSG 规则（Open Port，§23）。入站与出站共用这一份形状，只有 <see cref="Direction"/>
/// 与两个地址前缀的填法不同。
/// 执行器只认这份与 Provider 无关的描述，自行翻译成 ARM 的 SecurityRuleData —— 反之亦然。
/// </summary>
public sealed record NsgRuleDraft
{
    /// <summary>规则名（同一 NSG 内唯一）。</summary>
    public required string Name { get; init; }

    public required int Port { get; init; }

    public required NsgProtocol Protocol { get; init; }

    /// <summary>
    /// 源地址前缀，如 *、0.0.0.0/0、203.0.113.10/32、AzureLoadBalancer、VirtualNetwork。
    /// 入站规则上它是用户填的对端；出站规则上恒为 "*"（代表本机发起）。
    /// </summary>
    public required string SourcePrefix { get; init; }

    /// <summary>来源的展示文本（UI 列，不参与 Azure 请求）。</summary>
    public required string SourceDisplay { get; init; }

    public required int Priority { get; init; }

    /// <summary>规则方向。默认入站 —— 既有的 Open Port 行为不变。</summary>
    public NsgRuleDirection Direction { get; init; } = NsgRuleDirection.Inbound;

    /// <summary>
    /// 目标地址前缀。出站规则上它是用户填的对端；入站规则上恒为 "*"。
    /// 与 <see cref="SourcePrefix"/> 各管一侧，执行器**不做方向推断** —— 哪一侧是谁由构造方写明。
    /// </summary>
    public string DestinationPrefix { get; init; } = "*";

    /// <summary>目标的展示文本（出站用）。入站规则为 null，因为入站目标固定是 "*"。</summary>
    public string? DestinationDisplay { get; init; }
}

/// <summary>
/// Azure 规则 Resource ID 的拆装（<c>{nsgId}/securityRules/{name}</c>）。
///
/// 读路径一直按这个形状构造 RuleId，写路径过去却在 Open Port 里凭空编造 ID，
/// 导致真实账户下永远找不到目标 NSG。拆装集中在这里，读写两侧就不会再漂移。
/// </summary>
public static class NsgRuleIds
{
    private const string RuleSegment = "securityRules";

    /// <summary>拆出所属 NSG 与规则名；形状不符时返回 null（调用方据此报校验错误，而不是猜）。</summary>
    public static (string NsgId, string RuleName)? Split(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return null;
        }

        var segments = ruleId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!string.Equals(segments[i], RuleSegment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nsgId = "/" + string.Join('/', segments[..i]);
            return (nsgId, segments[i + 1]);
        }

        return null;
    }

    public static string Compose(string nsgId, string ruleName) =>
        $"{nsgId.TrimEnd('/')}/{RuleSegment}/{ruleName}";
}
