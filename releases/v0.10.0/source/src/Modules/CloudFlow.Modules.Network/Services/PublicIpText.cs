using System.Net;
using System.Net.Sockets;

namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 公网 IP 字面量的解析与规范化。
///
/// 单独抽出来是因为两条路径都要用同一套判定：回显端点返回的正文要在这里验成 IP
/// 才敢采信，而"我的当前 IP"写进 NSG 规则时又要按地址族给出正确的 CIDR。
/// 两处各写一份的话，迟早出现"验证用一套、拼 CIDR 用另一套"。
/// </summary>
public static class PublicIpText
{
    /// <summary>
    /// 把回显正文解析成 IP 字面量；不是干净的 IP 就返回 null。
    ///
    /// 这里比"看起来像 IP"严格得多，因为正文来自第三方端点，可能是门户 HTML、
    /// 错误页、被劫持的响应。**强校验的理由不是洁癖**：一个没被验出来的字符串会被
    /// 当成来源地址写进 NSG 规则，用户以为只放行了自己，实际放行的是别人。
    ///
    /// 判定方式用「解析后回写是否与原文一致」而不是正则：
    /// <see cref="IPAddress.TryParse"/> 相当宽松（"1.1.1" → 1.1.0.1、"12345" → 0.0.48.57），
    /// 但它的 <c>ToString</c> 是规范的，所以"能被解析 **且** 原文本来就是规范写法"
    /// 恰好等价于"这是一个规范 IP 字面量"，同时对 IPv4 / IPv6 两种地址族都成立 ——
    /// 而 IPv6 的正则要覆盖压缩写法、嵌入 IPv4 等形式，比这脆弱得多。
    /// </summary>
    public static string? ParseLiteral(string? body)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!IPAddress.TryParse(text, out var parsed))
        {
            return null;
        }

        return parsed.AddressFamily switch
        {
            // IPv4 必须逐段规范：拒绝 "1.1.1"、"01.02.03.04"、"12345"
            AddressFamily.InterNetwork => parsed.ToString() == text ? text : null,
            // IPv6 允许大小写差异（"2001:DB8::1" 是合法的），但不允许带 %scope 的作用域 ID：
            // 那不是一个可以写进 NSG 规则的地址。
            AddressFamily.InterNetworkV6 => text.Contains(':') && !text.Contains('%') ? text : null,
            _ => null
        };
    }

    /// <summary>
    /// IP 字面量 → 单主机 CIDR。IPv4 是 /32，IPv6 是 /128。
    ///
    /// 必须按地址族区分：过去这里恒拼 "/32"，对 IPv6 来说是错的
    /// （那会变成一个覆盖 2^96 个地址的网段）。
    /// </summary>
    public static string? ToCidr(string? ipLiteral)
    {
        var text = ParseLiteral(ipLiteral);
        if (text is null)
        {
            return null;
        }

        var prefix = IPAddress.Parse(text).AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{text}/{prefix}";
    }
}
