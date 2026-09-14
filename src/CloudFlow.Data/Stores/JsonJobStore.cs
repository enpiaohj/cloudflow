using System.Text.Json;
using System.Text.Json.Serialization;
using CloudFlow.Core.Operations;

namespace CloudFlow.Data.Stores;

/// <summary>
/// Job 历史持久化存储（JSON 文件，设计文档 §33 OperationJob）。
/// 应用重启后 Job 历史不丢失；写入采用临时文件原子替换。
/// </summary>
public sealed class JsonJobStore : IJobStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        // null 字段不落盘。除了省体积，更重要的是让"没有待审批请求"是文件里真的没有这一项，
        // 而不是一个值为 null 的 PendingRequest —— 后者读起来像"有，但是空的"。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly string _filePath;
    private Dictionary<Guid, OperationJob> _jobs;

    public event EventHandler<OperationJob>? JobChanged;

    /// <param name="filePath">
    /// 落盘位置；null 用约定的 <see cref="CloudFlowPaths.JobsFile"/>。
    /// 允许指定是为了能测"写到盘上、换个实例读回来"这条路径 ——
    /// 挂起审批能不能熬过重启，只有真的走一遍文件才说得准。
    /// </param>
    public JsonJobStore(string? filePath = null)
    {
        _filePath = filePath ?? CloudFlowPaths.JobsFile;
        _jobs = Load() ?? [];
        ReconcileInterruptedJobs();
    }

    /// <summary>
    /// 真实踩过的坑：应用在某个 Job 处于"正在执行"这类瞬时状态时被强制终止（未处理异常导致
    /// 进程崩溃），这个状态会原样留在磁盘上——重启后顶栏"任务进行中"徽标会一直显示这个 Job，
    /// 永远不会消失（这类瞬时状态不像 <see cref="JobStatus.WaitingApproval"/> 那样有"作废"出口）。
    /// 真实发生过：删除操作在 Azure 侧其实已经成功，但 UI 层的一处未处理异常在写回终态之前
    /// 就把进程带崩了，任务永久卡在 Running。
    /// </summary>
    /// <remarks>
    /// 一个新的 <see cref="JsonJobStore"/> 实例被构造，本身就说明上一个执行这些 Job 的进程已经
    /// 不在了——它们不可能还在真正执行。按"没有 Verify 不算完成"（设计文档 §45）的纪律，不能
    /// 悄悄当作成功；如实标记为失败并说明原因（可能其实已经执行成功，只是没能确认），用户可以
    /// 自行去 Azure 门户或刷新对应清单核实真实状态。<see cref="JobStatus.WaitingApproval"/> 是
    /// 合法的、有意保留跨重启的等待态（已有独立的作废出口），不受影响。
    /// </remarks>
    private void ReconcileInterruptedJobs()
    {
        var stale = _jobs.Values.Where(job => job.Status is
            JobStatus.Pending or JobStatus.Validating or JobStatus.AnalyzingImpact
            or JobStatus.Running or JobStatus.WaitingAzure or JobStatus.Verifying).ToList();
        if (stale.Count == 0)
        {
            return;
        }

        foreach (var job in stale)
        {
            job.Status = JobStatus.Failed;
            job.Error = "应用上次退出时该任务仍在执行中，实际结果未知（可能已经执行成功）。" +
                "请前往 Azure 门户或刷新对应清单确认真实状态。";
        }

        Save([.. _jobs.Values]);
    }

    public Task AddAsync(OperationJob job, CancellationToken ct = default)
    {
        Upsert(job);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(OperationJob job, CancellationToken ct = default)
    {
        Upsert(job);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _jobs.Clear();
        }

        // 空列表也要写一次盘：不写的话文件里还是旧内容，重启后历史全回来了。
        Save([]);
        return Task.CompletedTask;
    }

    public IReadOnlyList<OperationJob> GetAll()
    {
        lock (_lock)
        {
            return [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];
        }
    }

    public OperationJob? Find(Guid jobId)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }
    }

    private void Upsert(OperationJob job)
    {
        List<OperationJob> snapshot;
        lock (_lock)
        {
            _jobs[job.JobId] = job;
            snapshot = [.. _jobs.Values];
        }
        Save(snapshot);
        JobChanged?.Invoke(this, job);
    }

    private Dictionary<Guid, OperationJob>? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(_filePath);
            var jobs = JsonSerializer.Deserialize<List<OperationJob>>(json, Options);
            return jobs?.ToDictionary(j => j.JobId);
        }
        catch (JsonException)
        {
            // 历史文件损坏时不阻塞启动（审计仍有 JSONL 全量记录）
            return null;
        }
    }

    private void Save(List<OperationJob> jobs)
    {
        try
        {
            // 建 _filePath 自己的目录，而不是全局 Root：构造时允许指定任意路径
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(jobs, Options));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (IOException)
        {
            // 磁盘写入失败不阻塞操作流程；内存态仍可用
        }
    }
}
