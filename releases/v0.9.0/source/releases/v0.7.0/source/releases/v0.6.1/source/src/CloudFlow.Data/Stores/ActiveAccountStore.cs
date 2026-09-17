using System.Text.Json;

namespace CloudFlow.Data.Stores;

public sealed class ActiveAccountStore
{
    private readonly string _filePath;

    public ActiveAccountStore(string? filePath = null)
    {
        _filePath = filePath ?? CloudFlowPaths.ActiveAccountFile;
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            return document.RootElement.TryGetProperty("activeAccountId", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string accountId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { activeAccountId = accountId }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
