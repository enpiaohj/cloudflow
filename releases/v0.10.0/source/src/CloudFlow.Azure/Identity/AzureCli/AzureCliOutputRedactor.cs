using System.Text.RegularExpressions;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// Azure CLI 输出脱敏（P0 安全要求 §10）：任何进入日志 / 异常消息 / UI 的输出先经过这里。
/// 掩盖 JSON 形态的 token / secret / password / pat 值；原始输出本身不落盘、不记日志。
/// </summary>
public static partial class AzureCliOutputRedactor
{
    [GeneratedRegex("""
        "(?<key>[^"\r\n]*(token|secret|password|pat)[^"\r\n]*)"\s*:\s*"(?<value>[^"\r\n]*)"
        """, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex SecretJsonPattern();

    public const string Mask = "***";

    public static string Redact(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }

        return SecretJsonPattern().Replace(output, match =>
            $"\"{match.Groups["key"].Value}\": \"{Mask}\"");
    }
}
