namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>Azure CLI 进程执行契约（便于测试替身；实现见 AzureCliProcessRunner）。</summary>
public interface IAzureCliProcessRunner
{
    Task<AzureCliResult> RunAsync(AzureCliInvocation invocation, CancellationToken cancellationToken = default);
}
