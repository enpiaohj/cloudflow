using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

/// <summary>
/// 「清除本地任务历史」。真正的坑不在删文件，而在内存态 ——
/// 只删文件的话，下一次 UpdateAsync 会拿着内存里的全部 Job 重新落盘，
/// 用户看到的是"清完又自己回来了"。这一组就盯着这件事。
/// </summary>
public sealed class JobStoreClearTests : IDisposable
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

    private static OperationJob Job(string type) => new()
    {
        AccountId = "acc-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
        OperationType = type,
        Display = type,
        Status = JobStatus.Succeeded,
        Risk = RiskLevel.Low
    };

    [Fact]
    public async Task 清除后落盘文件为空且重新读回也是空()
    {
        var store = new JsonJobStore(FilePath);
        await store.AddAsync(Job("vm.start"));
        await store.AddAsync(Job("vm.restart"));
        Assert.Equal(2, store.GetAll().Count);

        await store.ClearAsync();

        Assert.Empty(store.GetAll());
        // 换个实例读同一个文件：这才是"清干净了"的判据，只断言内存态的话
        // 一个"内存清了但文件没写"的实现也会通过
        Assert.Empty(new JsonJobStore(FilePath).GetAll());
    }

    [Fact]
    public async Task 清除后再写入新Job_不会把已清除的Job一起写回来()
    {
        var store = new JsonJobStore(FilePath);
        var cleared = Job("vm.start");
        await store.AddAsync(cleared);
        await store.ClearAsync();

        // 这里刻意用 UpdateAsync 而不是 AddAsync：清除的实现如果只动了文件
        // 而没清内存字典，UpdateAsync 会把整份内存快照重新写出去
        await store.UpdateAsync(Job("vm.power_off"));

        var restored = new JsonJobStore(FilePath).GetAll();

        Assert.Single(restored);
        Assert.Equal("vm.power_off", restored[0].OperationType);
    }

}
