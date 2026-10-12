using System.Text.Json;
using System.Text.Json.Serialization;
using CloudFlow.Core.Operations;

namespace CloudFlow.Data.Stores;

/// <summary>
/// 审计日志文件实现：Append-Only JSONL，每行一条 AuditRecord。
/// </summary>
public sealed class AuditFileLog : IAuditLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();

    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        CloudFlowPaths.EnsureRoot();
        var line = JsonSerializer.Serialize(record, Options);
        lock (_lock)
        {
            File.AppendAllText(CloudFlowPaths.AuditFile, line + Environment.NewLine);
        }
        await Task.CompletedTask;
    }

    public IReadOnlyList<AuditRecord> Query(string? resourceId = null, int max = 200) =>
        QueryRecords(resourceId, max, null);

    public IReadOnlyList<AuditRecord> QueryForJob(Guid jobId, string resourceId) =>
        QueryRecords(resourceId, int.MaxValue, jobId);

    private IReadOnlyList<AuditRecord> QueryRecords(string? resourceId, int max, Guid? jobId)
    {
        string[] lines;
        lock (_lock)
        {
            if (!File.Exists(CloudFlowPaths.AuditFile)) return [];
            // 锁只覆盖文件快照；解析、过滤与排序不阻塞后续审计写入。
            lines = File.ReadAllLines(CloudFlowPaths.AuditFile);
        }
        List<AuditRecord> records = [];
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<AuditRecord>(line, Options);
                if (record is null || (jobId is not null && record.JobId != jobId)) continue;
                if (resourceId is not null && !string.Equals(record.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase)) continue;
                records.Add(record);
            }
            catch (JsonException) { /* 单行损坏不影响其他审计记录。 */ }
        }
        return [.. records.OrderByDescending(r => r.Timestamp).Take(max)];
    }
}
