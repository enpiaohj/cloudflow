using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.Terminal.Security;

/// <summary>
/// SSH 凭据库（ssh-credentials-library.json）的文档读写：全局凭据列表 + "最近使用"标量。
/// <para>
/// 只负责文件与文档结构（含 schema 版本判定、旧格式迁移、读时归一化）；
/// <b>策略</b>（认证方式不变式、保险库编排水）在 <see cref="SshCredentialService"/>。
/// </para>
/// <para>
/// 名称唯一性检查放在这里的读写锁<b>内部</b>，因为它是文档自身的约束 ——
/// 放到服务层会变成"先查后写"的两次加锁，并发新建同名凭据时两边都能通过检查。
/// </para>
/// </summary>
public sealed class SshCredentialLibrary
{
    private readonly string _filePath;
    private readonly ILogger _logger;

    /// <summary>保护读改写全过程，避免并发写入互相覆盖。</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SshCredentialLibrary(string filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullLogger.Instance;

        // 与同目录的 DpapiCredentialVault 一致：构造时就把目录备好，
        // 免得"第一次写入才发现目录不存在"，而那时通常已经在某个提交路径中间。
        var directory = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 库文件路径。命名刻意不叫 <c>Path</c> —— 那会在类内遮蔽 <see cref="System.IO.Path"/>，
    /// 让本类里的 <c>Path.GetDirectoryName</c> 之类突然指向一个字符串属性。
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>库文件是否已存在。存在即视为"旧格式已经迁移过"。</summary>
    public bool Exists() => File.Exists(_filePath);

    public async Task<IReadOnlyList<SshCredential>> ListAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);

            // 按名称排序：设置页列表与连接框下拉共用这一个次序，两处显示顺序必然一致
            return document.Credentials
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<SshCredential?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            return document.Credentials.FirstOrDefault(c => c.Id == id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 按 <see cref="SshCredential.Id"/> upsert。<b>返回被覆盖的旧记录</b>
    /// （调用方据此清理保险库里的旧密文），没有旧记录时返回 <c>null</c>。
    /// </summary>
    /// <exception cref="InvalidOperationException">已存在同名（忽略大小写）的其他凭据。</exception>
    public async Task<SshCredential?> SaveAsync(SshCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.Name);

        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);

            var clash = document.Credentials.FirstOrDefault(c =>
                c.Id != credential.Id &&
                string.Equals(c.Name, credential.Name, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                throw new InvalidOperationException($"已存在同名凭据「{clash.Name}」。请换一个名称。");
            }

            var existing = document.Credentials.FirstOrDefault(c => c.Id == credential.Id);
            document.Credentials.RemoveAll(c => c.Id == credential.Id);
            document.Credentials.Add(credential);

            await SaveDocumentAsync(document, ct);
            return existing;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 删除一条凭据，返回被删除的记录（调用方据此清理保险库密文）；不存在时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 即使删到一条不剩，<b>仍然把文件写成空列表</b>，绝不删除文件 ——
    /// 文件一旦消失，<see cref="MigrateFromLegacyAsync"/> 会把旧格式里的记录重新迁一遍，
    /// 用户"删掉的凭据"就复活了。这条与迁移的触发闸门是同一个机制，不要拆开改。
    /// </remarks>
    public async Task<SshCredential?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);

            var removed = document.Credentials.FirstOrDefault(c => c.Id == id);
            if (removed is null)
            {
                return null;
            }

            document.Credentials.RemoveAll(c => c.Id == id);

            // "最近使用"指向被删的凭据时必须一起清掉，否则下次打开连接框会预选一个
            // 已经不存在的主键。与删除同在这一次写入里完成。
            if (document.LastUsedCredentialId == id)
            {
                document.LastUsedCredentialId = null;
            }

            // 记住这条凭据的 VM 一并回到"未指定"状态，理由同上。
            // （RemoteFlow 用 SQL 的 ON DELETE SET NULL 达成同一效果：不阻止删除、不级联删连接，
            //   只把引用清空。删除前的"影响 N 台"提示在设置页，不在这里。）
            var affected = document.DefaultCredentialByVm
                .Where(pair => pair.Value == id)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in affected)
            {
                document.DefaultCredentialByVm.Remove(key);
            }

            await SaveDocumentAsync(document, ct);
            return removed;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>读取"最近使用"的凭据 Id。可能是陈旧的（指向已删除的记录），由调用方裁决。</summary>
    public async Task<Guid?> GetLastUsedIdAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            return document.LastUsedCredentialId;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>写入"最近使用"的凭据 Id；传 <c>null</c> 表示清空。</summary>
    public async Task SetLastUsedIdAsync(Guid? id, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            if (document.LastUsedCredentialId == id)
            {
                return;   // 没变化就不写盘：这是每次连接都会走的路径，别为它多写一次文件
            }

            document.LastUsedCredentialId = id;
            await SaveDocumentAsync(document, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 该 VM 记住的凭据 Id；没有记录、或记录指向已删除的凭据时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 指向已删除凭据的条目按"没有记录"处理（与 <see cref="GetLastUsedIdAsync"/> 同一纪律）：
    /// 让调用方回退到全局最近使用，而不是拿着一个死主键去预选。
    /// 主动清理在 <see cref="DeleteAsync"/> 里与删除同一次写入完成，所以这里遇到陈旧项
    /// 只可能是文件被手工编辑过。
    /// </remarks>
    public async Task<Guid?> GetDefaultCredentialIdAsync(string vmResourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmResourceId))
        {
            return null;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);

            if (FindVmKey(document, vmResourceId) is not { } key ||
                !document.DefaultCredentialByVm.TryGetValue(key, out var id))
            {
                return null;
            }

            return document.Credentials.Any(c => c.Id == id) ? id : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>记录该 VM 用哪条凭据；传 <c>null</c> 表示清除这条记录。</summary>
    public async Task SetDefaultCredentialIdAsync(
        string vmResourceId, Guid? credentialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmResourceId))
        {
            return;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);

            // 键沿用文件里已有的那个写法，免得同一台机器因大小写差异攒成两条记录
            var key = FindVmKey(document, vmResourceId) ?? vmResourceId;

            if (credentialId is null)
            {
                if (document.DefaultCredentialByVm.Remove(key))
                {
                    await SaveDocumentAsync(document, ct);
                }

                return;
            }

            if (document.DefaultCredentialByVm.TryGetValue(key, out var current) &&
                current == credentialId.Value)
            {
                return;   // 没变化就不写盘：这是每次连接都会走的路径，别为它多写一次文件
            }

            document.DefaultCredentialByVm[key] = credentialId.Value;
            await SaveDocumentAsync(document, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 有多少台虚拟机记住了这条凭据 —— 删除前的影响提示用。
    /// </summary>
    /// <remarks>
    /// 与 RemoteFlow 的 <c>CountByCredentialAsync</c> 同一用途：<b>只用来告知，不用来阻止删除</b>。
    /// 阻止删除会让用户没法清理一条在别处被引用的凭据，而"清空引用"本身是无损的
    /// （删完那些 VM 回到"未指定凭据"，下次连接重新选即可）。
    /// </remarks>
    public async Task<int> CountVmsUsingAsync(Guid credentialId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            return document.DefaultCredentialByVm.Count(pair => pair.Value == credentialId);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 在文件里找该 ResourceId 对应的键。
    /// 字典反序列化用的是默认比较器（区分大小写），而 ResourceId 在不同来源上的大小写
    /// 并不保证一致（终端面板的标签去重就用的 OrdinalIgnoreCase）—— 所以查的时候不依赖比较器。
    /// </summary>
    private static string? FindVmKey(SshCredentialLibraryDocument document, string vmResourceId) =>
        document.DefaultCredentialByVm.Keys
            .FirstOrDefault(key => string.Equals(key, vmResourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把旧格式（每 VM 一条的 ssh-credentials.json）迁移成全局凭据库。
    /// 返回迁移条数；不需要迁移时返回 0。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>触发闸门 = 本库文件是否存在</b>，不额外引入 marker 文件。于是
    /// "迁移只发生一次"与"删光凭据后旧记录不复活"是同一个机制。
    /// </para>
    /// <para>
    /// 迁移<b>只搬运引用键，不重新加解密</b>：保险库（ssh-vault.dat）一个字节都不碰。
    /// 这是迁移风险最小的关键 —— 整条路径上没有任何 <c>RetrieveSecretAsync</c>，
    /// 不会为了一批记录把明文批量拉进内存。
    /// </para>
    /// <para>
    /// 旧文件全程<b>只读</b>：不写入、不删除、迁移后不再被读取。
    /// </para>
    /// </remarks>
    public async Task<int> MigrateFromLegacyAsync(string legacyPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);

        await _mutex.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath) || !File.Exists(legacyPath))
            {
                return 0;
            }

            List<SshCredentialMeta>? legacy;
            try
            {
                legacy = await AtomicJson.LoadAsync<List<SshCredentialMeta>>(legacyPath, ct);
            }
            catch (JsonException ex)
            {
                // 旧文件坏了不该让新库跟着不可用 —— 但也不能当它不存在而静默丢掉记录，
                // 所以显式失败，交由上层提示用户自行处理（删掉或改名旧文件即可继续）。
                _logger.LogError(ex, "旧格式 SSH 凭据文件已损坏，无法迁移：{LegacyPath}", legacyPath);
                throw new InvalidOperationException(
                    $"旧格式的 SSH 凭据文件已损坏，无法迁移：{legacyPath}。" +
                    "请改名或删除该文件后重试（凭据库尚未创建，不影响现有数据）。", ex);
            }

            if (legacy is null || legacy.Count == 0)
            {
                // 不创建库文件：留待将来再判。代价只是每次启动多一次 File.Exists，几微秒。
                return 0;
            }

            var document = new SshCredentialLibraryDocument();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var meta in legacy)
            {
                var name = MakeUniqueName(DeriveName(meta.ResourceId, meta.Username), usedNames);
                usedNames.Add(name);

                document.Credentials.Add(new SshCredential
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Username = string.IsNullOrWhiteSpace(meta.Username) ? "azureuser" : meta.Username,
                    AuthType = meta.AuthType,

                    // 引用键原样搬运 —— 保险库里的密文不需要重新加解密
                    SecretReference = meta.SecretReference,
                    KeyReference = meta.KeyReference,
                    PrivateKeyPath = meta.PrivateKeyPath,

                    // 刻意留空，不写 ResourceId：那等于把"VM → 凭据"的关联换个地方存，
                    // 与"凭据不绑定主机"的口径相悖。名字已足够让用户认出它来自哪台机器。
                    Description = "",

                    CreatedAt = meta.SavedAt,
                    UpdatedAt = meta.SavedAt,
                });
            }

            await SaveDocumentAsync(document, ct);

            // 只记条数，不记任何凭据内容
            _logger.LogInformation("已从旧格式迁移 {Count} 条 SSH 凭据到凭据库", document.Credentials.Count);
            return document.Credentials.Count;
        }
        finally
        {
            _mutex.Release();
        }
    }

    // ── 文档存取 ──────────────────────────────────────────────────

    private async Task<SshCredentialLibraryDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new SshCredentialLibraryDocument();
        }

        SshCredentialLibraryDocument? document;
        try
        {
            document = await AtomicJson.LoadAsync<SshCredentialLibraryDocument>(_filePath, ct);
        }
        catch (JsonException ex)
        {
            // 与保险库同一纪律：不静默重建、不清空。损坏就显式失败并让用户处理。
            _logger.LogError(ex, "SSH 凭据库文件已损坏：{FilePath}", _filePath);
            throw new InvalidOperationException(
                $"SSH 凭据库文件已损坏：{_filePath}。请从备份恢复，或改名该文件后重新录入凭据。", ex);
        }

        if (document is null)
        {
            return new SshCredentialLibraryDocument();
        }

        if (document.SchemaVersion > SshCredentialLibraryDocument.CurrentSchemaVersion)
        {
            // 高版本写入的文件对本版本是不可理解的：绝不改写、绝不按旧结构解释。
            _logger.LogError("SSH 凭据库由更高版本写入：schemaVersion={Version}", document.SchemaVersion);
            throw new InvalidOperationException(
                $"SSH 凭据库文件由更高版本的 CloudFlow 写入（schemaVersion={document.SchemaVersion}，" +
                $"本版本最高支持 {SshCredentialLibraryDocument.CurrentSchemaVersion}）：{_filePath}。" +
                "请升级 CloudFlow，或从备份恢复。本版本不会改写该文件。");
        }

        foreach (var credential in document.Credentials)
        {
            Normalize(credential);
        }

        return document;
    }

    private Task SaveDocumentAsync(SshCredentialLibraryDocument document, CancellationToken ct)
    {
        // 永远按当前版本号写。LoadAsync 允许读入低版本文件，若原样回写就会留下
        // "内容已经是新版、版本号还写着旧版"的错配 —— 而版本号正是用来防这件事的：
        // 旧版程序看到低版本号会照常读写，然后把它读不懂的新字段静默丢掉。
        document.SchemaVersion = SshCredentialLibraryDocument.CurrentSchemaVersion;
        return AtomicJson.SaveAsync(_filePath, document, ct);
    }

    /// <summary>
    /// 读时归一化：丢弃与认证方式矛盾的多余引用。
    /// 只丢弃而不是抛异常 —— 一条脏记录不该让整个凭据库不可用。
    /// </summary>
    private void Normalize(SshCredential credential)
    {
        // 唯一会自相矛盾的情形：密码方式却带着私钥引用（改认证方式没清干净、或手工编辑过）。
        // 留着它会在建连时被误当成"这条是私钥凭据"。
        // 反方向（私钥方式带着 SecretReference）是合法的 —— 那是 passphrase。
        if (credential.AuthType == SshAuthType.Password && credential.KeyReference is not null)
        {
            _logger.LogWarning(
                "凭据 {Name} 是密码方式却残留私钥引用，已忽略该引用", credential.Name);
            credential.KeyReference = null;
            credential.PrivateKeyPath = null;
        }
    }

    /// <summary>从 Azure ResourceId 末段派生显示名，例如 <c>…/virtualMachines/appscloud</c> → <c>appscloud</c>。</summary>
    private static string DeriveName(string? resourceId, string? username)
    {
        var trimmed = (resourceId ?? string.Empty).TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var last = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        if (!string.IsNullOrWhiteSpace(last))
        {
            return last.Trim();
        }

        // 畸形 ID 的兜底：退回用户名，再退回一个中性名字
        return string.IsNullOrWhiteSpace(username) ? "凭据" : username.Trim();
    }

    /// <summary>撞名时追加「 (2)」「 (3)」…（末段相同只可能出现在多订阅同名 VM）。</summary>
    private static string MakeUniqueName(string name, HashSet<string> used)
    {
        if (!used.Contains(name))
        {
            return name;
        }

        // used 是有限集合，必然终止
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
