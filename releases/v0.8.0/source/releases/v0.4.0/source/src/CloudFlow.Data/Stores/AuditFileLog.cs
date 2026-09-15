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

    public IReadOnlyList<AuditRecord> Query(string? resourceId = null, int max = 200)
    {
        if (!File.Exists(CloudFlowPaths.AuditFile))
        {
            return [];
        }

        List<AuditRecord> records = [];
        lock (_lock)
        {
            foreach (var line in File.ReadLines(CloudFlowPaths.AuditFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    var record = JsonSerializer.Deserialize<AuditRecord>(line, Options);
                    if (record is null)
                    {
                        continue;
                    }
                    if (resourceId is not null &&
                        !string.Equals(record.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    records.Add(record);
                }
                catch (JsonException)
                {
                    // 单行损坏不影响整体审计读取
                }
            }
        }

        return [.. records
            .OrderByDescending(r => r.Timestamp)
            .Take(max)];
    }
}
