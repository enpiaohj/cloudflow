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

    /// <summary>
    /// 清空全部 Job 历史。
    ///
    /// <para>
    /// **只清 Job。** 账户登录状态、已保存的 Scope、审计日志（<c>audit.jsonl</c>）都不归它管，
    /// 也必然不在同一个文件里 —— 审计是合规记录（设计文档 §34），清界面上的历史列表
    /// 不等于销毁审计，实现方不要顺手把它们一起删了。
    /// </para>
    /// <para>
    /// **必须同时清内存态**：只删文件的话，下一次 <see cref="UpdateAsync"/> 会拿着内存里的
    /// 全部 Job 重新落盘，用户看到的是"清完又自己回来了"。
    /// </para>
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>按时间倒序的全部 Job。</summary>
    IReadOnlyList<OperationJob> GetAll();

    OperationJob? Find(Guid jobId);
}
