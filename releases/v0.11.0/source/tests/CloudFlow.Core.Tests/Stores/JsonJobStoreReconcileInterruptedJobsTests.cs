using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

/// <summary>
/// 真实踩过的坑：应用在某个 Job 处于"正在执行"这类瞬时状态时被强制终止（未处理异常导致
/// 进程崩溃），这个状态会原样留在磁盘上——重启后顶栏"任务进行中"徽标会一直显示这个 Job，
/// 永远不会消失。真实发生过：删除操作在 Azure 侧其实已经成功，但 UI 层的一处未处理异常在
/// 写回终态之前就把进程带崩了，任务永久卡在 Running。
/// </summary>
public sealed class JsonJobStoreReconcileInterruptedJobsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "jobs.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OperationJob JobWith(JobStatus status) => new()
    {
        AccountId = "acc-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/networkWatchers/nw1",
        OperationType = "resource.delete",
        Display = "删除资源 NetworkWatcher_southeastasia",
        Status = status
    };

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Validating)]
    [InlineData(JobStatus.AnalyzingImpact)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.WaitingAzure)]
    [InlineData(JobStatus.Verifying)]
    public async Task 构造新实例时把上次遗留的执行中状态改判为失败(JobStatus interruptedStatus)
    {
        var job = JobWith(interruptedStatus);
        await new JsonJobStore(FilePath).AddAsync(job);

        // 新实例 = 模拟应用重启：上一个执行这些 Job 的进程已经不在了，不可能还在真正执行。
        var restored = new JsonJobStore(FilePath).Find(job.JobId);

        Assert.NotNull(restored);
        Assert.Equal(JobStatus.Failed, restored!.Status);
        Assert.False(string.IsNullOrWhiteSpace(restored.Error));
    }

    [Fact]
    public async Task WaitingApproval是合法的跨重启等待态_不受影响()
    {
        var job = JobWith(JobStatus.WaitingApproval);
        await new JsonJobStore(FilePath).AddAsync(job);

        var restored = new JsonJobStore(FilePath).Find(job.JobId);

        Assert.Equal(JobStatus.WaitingApproval, restored!.Status);
        Assert.Null(restored.Error);
    }

    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    public async Task 已经是终态的Job不受影响(JobStatus terminalStatus)
    {
        var job = JobWith(terminalStatus);
        job.Summary = "已完成，结果已验证。";
        await new JsonJobStore(FilePath).AddAsync(job);

        var restored = new JsonJobStore(FilePath).Find(job.JobId);

        Assert.Equal(terminalStatus, restored!.Status);
        Assert.Null(restored.Error);
        Assert.Equal("已完成，结果已验证。", restored.Summary);
    }

    [Fact]
    public async Task 改判结果会落盘_不是只改内存()
    {
        var job = JobWith(JobStatus.Running);
        await new JsonJobStore(FilePath).AddAsync(job);
        _ = new JsonJobStore(FilePath); // 触发一次改判并落盘

        // 第三个实例：如果改判只停在内存里没真的存盘，这里会读回原始的 Running。
        var restored = new JsonJobStore(FilePath).Find(job.JobId);

        Assert.Equal(JobStatus.Failed, restored!.Status);
    }
}
