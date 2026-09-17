namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>Azure CLI Bridge 执行失败。消息内容必须已经脱敏，不携带 Token / Secret。</summary>
public sealed class AzureCliException : Exception
{
    public int? ExitCode { get; }

    /// <summary>
    /// 是否由**可重试的网络瞬断**引起（连接被重置 / 代理握手失败等）。上层据此决定处理策略：
    /// 瞬断时不应把账户登记、内存缓存等**永久性状态**一并丢弃——账户本身没问题，是网络问题。
    /// </summary>
    public bool IsTransientNetwork { get; init; }

    public AzureCliException(string message, int? exitCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }
}
