namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>Azure CLI Bridge 执行失败。消息内容必须已经脱敏，不携带 Token / Secret。</summary>
public sealed class AzureCliException : Exception
{
    public int? ExitCode { get; }

    public AzureCliException(string message, int? exitCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }
}
