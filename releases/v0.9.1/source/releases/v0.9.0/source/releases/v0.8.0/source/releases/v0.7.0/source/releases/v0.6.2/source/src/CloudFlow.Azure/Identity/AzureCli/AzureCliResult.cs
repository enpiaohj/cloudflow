namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>Azure CLI 进程执行结果。原始输出仅在内存中流转，任何日志路径必须先脱敏。</summary>
public sealed record AzureCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
