namespace CloudFlow.Terminal.Security;

/// <summary>
/// SSH 凭据元数据存储（ssh-credentials.json）。
/// 文件里只有引用键、用户名等非秘密；明文在 <see cref="DpapiCredentialVault"/>。
/// 覆盖保存产生的新引用意味着旧 Secret 作废 —— 调用方需协调 Vault 删除旧 Secret（防残留）。
/// </summary>
public sealed class SshCredentialStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SshCredentialStore(string path)
    {
        _path = path;
    }

    public async Task<SshCredentialMeta?> FindAsync(string resourceId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<SshCredentialMeta>>(_path, ct) ?? [];
            return list.FirstOrDefault(m => m.ResourceId == resourceId);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>返回被覆盖条目的旧引用（供调用方清理 Vault），没有旧条目时为空。</summary>
    public async Task<SshCredentialMeta?> SaveAsync(SshCredentialMeta meta, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<SshCredentialMeta>>(_path, ct) ?? [];
            var existing = list.FirstOrDefault(m => m.ResourceId == meta.ResourceId);
            list.RemoveAll(m => m.ResourceId == meta.ResourceId);
            list.Add(meta);
            await AtomicJson.SaveAsync(_path, list, ct);
            return existing;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteAsync(string resourceId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<SshCredentialMeta>>(_path, ct) ?? [];
            list.RemoveAll(m => m.ResourceId == resourceId);
            await AtomicJson.SaveAsync(_path, list, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
