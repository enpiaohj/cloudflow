namespace CloudFlow.Terminal.Security;

/// <summary>
/// SSH 主机指纹记录（known-hosts.json）。首次连接确认后记录；同主机+端口重连覆盖更新。
/// 指纹是公开信息（远端主动出示），本文件不含秘密。
/// </summary>
public sealed class KnownHostStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public KnownHostStore(string path)
    {
        _path = path;
    }

    public async Task<KnownHostEntry?> FindAsync(string host, int port, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<KnownHostEntry>>(_path, ct) ?? [];
            return list.FirstOrDefault(e => e.Host == host && e.Port == port);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task AddAsync(KnownHostEntry entry, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<KnownHostEntry>>(_path, ct) ?? [];
            list.RemoveAll(e => e.Host == entry.Host && e.Port == entry.Port);
            list.Add(entry);
            await AtomicJson.SaveAsync(_path, list, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task RemoveAsync(string host, int port, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var list = await AtomicJson.LoadAsync<List<KnownHostEntry>>(_path, ct) ?? [];
            list.RemoveAll(e => e.Host == host && e.Port == port);
            await AtomicJson.SaveAsync(_path, list, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
