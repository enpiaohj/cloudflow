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
