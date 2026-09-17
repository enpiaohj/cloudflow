using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudFlow.Data.Stores;

/// <summary>JSON 文件读写基类（简单、可回滚、可迁移，P1 数据量足够）。</summary>
public abstract class JsonFileStore<T>
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    protected abstract string FilePath { get; }

    protected virtual T? Load()
    {
        if (!File.Exists(FilePath))
        {
            return default;
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    protected virtual void Save(T value)
    {
        CloudFlowPaths.EnsureRoot();
        // 先写临时文件再原子替换，避免写入中断损坏数据（可回滚原则）
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
