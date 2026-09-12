using System.Collections.ObjectModel;

namespace CloudFlow.Core.Operations;

/// <summary>
/// Job 存储（P1 内存实现，后续由 Data 层持久化）。
/// </summary>
public interface IJobStore
{
    /// <summary>Job 新增或更新事件（Jobs Center 实时刷新）。</summary>
    event EventHandler<OperationJob>? JobChanged;

    Task AddAsync(OperationJob job, CancellationToken ct = default);

    Task UpdateAsync(OperationJob job, CancellationToken ct = default);

    /// <summary>按时间倒序的全部 Job。</summary>
    IReadOnlyList<OperationJob> GetAll();

    OperationJob? Find(Guid jobId);
}
