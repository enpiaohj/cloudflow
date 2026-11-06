using System.Text.RegularExpressions;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// Azure CLI 失败的归因与措辞。
///
/// 真实踩过的坑：`az account get-access-token` 在**临时网络波动**（代理握手时
/// ConnectionReset，WinError 10054）下整条链路失败，CloudFlow 直接判成账户不可用，
/// 还把整段 az 的 Python traceback 原样抛给了用户——既不是账户问题，又不是凭证问题，
/// 消息还全是噪音。这里把"网络瞬断"这一类失败识别出来：给它们重试的机会，并给出一句
/// 可行动的收敛措辞，而不是把实现细节甩给用户。
/// </summary>
public static partial class AzureCliFailure
{
    /// <summary>
    /// 网络瞬断的信号（stderr 里命中任意一条即判定为可重试的网络问题）。
    /// 全小写匹配；只收录"重试后很可能自愈"的瞬态网络信号，不把 Azure 权限 / 参数 /
    /// 租户错误误判成可重试。
    /// </summary>
    [GeneratedRegex(
        """
        connection\s*reset
        |connect\s*aborted
        |connection\s*aborted
        |connection\s*error
        |remote\s*host\s*forcibly\s*closed
        |winerror\s*10054
        |\b10054\b
        |\b10060\b            # connect timed out
        |\b11001\b            # getaddrinfo failed（域名解析失败）
        |getaddrinfo\s*failed
        |name\s*or\s*service\s*not\s*known
        |timed?\s*out
        |timeout\s*error
        |proxy\s*error
        |连接被重置
        |远程主机强制关闭
        |远程主机强迫关闭
        |一个现有的连接
        |连接已中止
        |连接被强制关闭
        |无法连接到远程服务器
        """,
        RegexOptions.IgnoreCase
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.ExplicitCapture
        | RegexOptions.Compiled)]
    private static partial Regex TransientNetworkPattern();

    public sealed record Classification(bool IsTransientNetwork);

    public static Classification Classify(int? exitCode, string stderr)
    {
        _ = exitCode;
        if (string.IsNullOrEmpty(stderr))
        {
            return new Classification(false);
        }

        return new Classification(TransientNetworkPattern().IsMatch(stderr));
    }

    /// <summary>
    /// 收敛成一句可行动的措辞。瞬断 → 说明这是网络问题、不是账户/凭证问题，并给出重试方向；
    /// 其它失败 → 沿用原有"退出码 + 脱敏详情"的格式，不做退步。
    /// </summary>
    public static string Describe(string operationName, int? exitCode, string redactedStderr)
    {
        if (Classify(exitCode, redactedStderr).IsTransientNetwork)
        {
            return operationName switch
            {
                "获取访问令牌" =>
                    "网络连接中断，无法获取访问令牌。这通常是暂时的网络波动或代理不稳定，并非账户或"
                    + "凭证的问题；CloudFlow 已自动重试仍失败。请检查网络连接（或代理）后重试；若连续"
                    + "失败，可到设置中重新登录该账户。",
                "获取订阅列表" =>
                    "网络连接中断，无法获取订阅列表。这通常是暂时的网络波动或代理不稳定；"
                    + "请检查网络连接（或代理）后重试。",
                "个人账户登录" =>
                    "网络连接中断，登录过程中断。请检查网络连接（或代理）后重新登录。",
                _ =>
                    "网络连接中断，操作未完成。这通常是暂时的网络波动或代理不稳定；"
                    + "请检查网络连接后重试。"
            };
        }

        return $"{operationName}失败（退出码 {exitCode ?? -1}）：{redactedStderr}";
    }
}