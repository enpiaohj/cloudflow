using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudFlow.Terminal.Security;

/// <summary>SSH 认证方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SshAuthType
{
    Password,
    PrivateKey
}

/// <summary>
/// 一台 VM 已保存的 SSH 凭据<b>元数据</b>。
/// 引用键指向 DpapiCredentialVault 里的密文；本文件不含任何明文 Secret。
/// </summary>
public sealed class SshCredentialMeta
{
    /// <summary>关联的 VM 资源 ID（Azure Resource ID，主键）。</summary>
    public required string ResourceId { get; init; }

    public string Username { get; set; } = "azureuser";

    public SshAuthType AuthType { get; set; } = SshAuthType.PrivateKey;

    /// <summary>密码密文引用（Password 方式）。可空。</summary>
    public string? SecretReference { get; set; }

    /// <summary>私钥内容密文引用（PrivateKey 方式）。可空。</summary>
    public string? KeyReference { get; set; }

    /// <summary>记住的私钥文件位置（非秘密，方便下次选文件）。可空。</summary>
    public string? PrivateKeyPath { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>一台主机的 SSH Host Key 指纹记录（指纹是公开信息，非秘密）。</summary>
public sealed class KnownHostEntry
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string KeyAlgorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>JSON 文件的原子读写：临时文件 + 落盘 + 整体替换，进程被杀不留半个文件。</summary>
public static class AtomicJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task<T?> LoadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct);
    }

    public static async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
