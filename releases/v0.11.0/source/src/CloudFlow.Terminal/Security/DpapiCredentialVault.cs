using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Terminal.Security;

/// <summary>
/// 基于 Windows DPAPI（CurrentUser）的凭据保险库。
/// <para>
/// 存储模型（照 RemoteFlow 逻辑移植）：
/// 元数据 JSON（ssh-credentials.json）里只存 GUID 引用键；
/// 明文 Secret 经 DPAPI 加密后放独立的 <c>ssh-vault.dat</c>。
/// 密文被复制到其他机器 / 其他 Windows 账户均无法解密。
/// 本类是整个终端功能<b>唯一</b>接触明文 Secret 的组件，任何方法不得把 Secret 写进日志。
/// </para>
/// </summary>
public sealed class DpapiCredentialVault
{
    /// <summary>DPAPI 附加熵：与用户主密钥共同参与加解密，
    /// 使密文即便被放到同一用户的其他应用上下文也无法直接解开。</summary>
    private static readonly byte[] Entropy = "CloudFlow.SshCredentialVault.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _vaultPath;
    private readonly ILogger _logger;

    /// <summary>保护读改写全过程，避免并发写入互相覆盖。</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DpapiCredentialVault(string vaultPath, ILogger logger)
    {
        _vaultPath = vaultPath;
        _logger = logger;

        var directory = Path.GetDirectoryName(vaultPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>生成引用键：GUID，避免从键名反推凭据用途。</summary>
    public static string CreateReference(string purpose) => $"{purpose}:{Guid.NewGuid():N}";

    public async Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            store[reference] = Protect(secret);
            await SaveStoreAsync(store, ct);

            // 只记录引用键，绝不记录 Secret 本身。
            _logger.LogInformation("已保存凭据 Secret，引用键 {Reference}", reference);
            return reference;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            return store.TryGetValue(reference, out var cipherText) ? Unprotect(cipherText, reference) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteSecretAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var store = await LoadStoreAsync(ct);
            if (store.Remove(reference))
            {
                await SaveStoreAsync(store, ct);
                _logger.LogInformation("已删除凭据 Secret，引用键 {Reference}", reference);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    // ── DPAPI ─────────────────────────────────────────────────────

    private static string Protect(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            // 明文字节用后立即清零，缩短其在内存中的存活窗口。
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private string? Unprotect(string cipherText, string reference)
    {
        byte[]? plainBytes = null;
        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);
            plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            // 常见原因：vault 来自其他 Windows 用户 / 其他机器，主密钥对不上。
            // 只记引用键与失败事实，不记录任何密文或明文。
            _logger.LogError(ex, "无法解密引用键 {Reference} 对应的 Secret，可能来自其他 Windows 账户或机器", reference);
            return null;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "引用键 {Reference} 对应的 Secret 密文格式损坏", reference);
            return null;
        }
        finally
        {
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    // ── 文件存取 ──────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> LoadStoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_vaultPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_vaultPath);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct) ?? [];
        }
        catch (JsonException ex)
        {
            // 不静默重建：直接失败并让上层提示用户，避免无声丢失全部凭据。
            _logger.LogError(ex, "凭据保险库文件已损坏：{Path}", _vaultPath);
            throw new InvalidOperationException(
                $"凭据保险库文件已损坏：{_vaultPath}。请从备份恢复，或删除该文件后重新录入凭据。", ex);
        }
    }

    /// <summary>原子写入：先写临时文件并落盘再整体替换，进程被杀不会留下半个文件。</summary>
    private async Task SaveStoreAsync(Dictionary<string, string> store, CancellationToken ct)
    {
        var tempPath = _vaultPath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, store, JsonOptions, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_vaultPath))
        {
            File.Replace(tempPath, _vaultPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _vaultPath);
        }
    }
}
