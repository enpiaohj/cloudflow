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
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private Dictionary<Guid, OperationJob> _jobs;

    public event EventHandler<OperationJob>? JobChanged;

    public JsonJobStore()
    {
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
        if (!File.Exists(CloudFlowPaths.JobsFile))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(CloudFlowPaths.JobsFile);
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
            CloudFlowPaths.EnsureRoot();
            var tmp = CloudFlowPaths.JobsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(jobs, Options));
            File.Move(tmp, CloudFlowPaths.JobsFile, overwrite: true);
        }
        catch (IOException)
        {
            // 磁盘写入失败不阻塞操作流程；内存态仍可用
        }
    }
}
