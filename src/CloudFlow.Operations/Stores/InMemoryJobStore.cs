using System.Collections.Concurrent;
using CloudFlow.Core.Operations;

namespace CloudFlow.Operations.Stores;

/// <summary>
/// 内存 Job 存储（P1）。历史持久化在后续阶段接入 Data 层。
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<Guid, OperationJob> _jobs = new();

    public event EventHandler<OperationJob>? JobChanged;

    public Task AddAsync(OperationJob job, CancellationToken ct = default)
    {
        _jobs[job.JobId] = job;
        JobChanged?.Invoke(this, job);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(OperationJob job, CancellationToken ct = default)
    {
        _jobs[job.JobId] = job;
        JobChanged?.Invoke(this, job);
        return Task.CompletedTask;
    }

    public IReadOnlyList<OperationJob> GetAll() =>
        [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];

    public OperationJob? Find(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;
}
