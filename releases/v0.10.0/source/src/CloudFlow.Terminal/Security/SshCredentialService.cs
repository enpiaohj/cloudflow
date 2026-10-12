using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.Terminal.Security;

/// <summary>
/// SSH 凭据库的<b>唯一</b>读写入口：编排"元数据文件 + DPAPI 保险库"这两个存储，
/// 并强制三条不变式。
/// </summary>
/// <remarks>
/// <para><b>提交点顺序（有意偏离 RemoteFlow，别照抄回去）：</b></para>
/// <list type="number">
/// <item>先把新密文写进保险库 —— 此刻还没有任何记录引用它，失败即整体失败，旧状态完好。</item>
/// <item>再写元数据 —— <b>这一步是提交点</b>。</item>
/// <item>最后尽力删除旧密文，失败只记日志、不抛。</item>
/// </list>
/// <para>
/// RemoteFlow 是"先删旧密文再写元数据"，中途挂掉会留下<b>元数据指向已删除引用</b>的记录 ——
/// 用户在列表里看得见它，点下去必然连不上，而提示还是误导性的"认证失败"。
/// 本实现的失败方向是"留下一个无人引用的孤儿密文"：不可达、无害、只多占几百字节。
/// 删除同理，先删元数据再清密文。
/// </para>
/// <para>
/// <b>本类不接触明文 Secret 之外的任何东西，也不会把明文写进日志、异常消息或 ToString()。</b>
/// 唯一的明文出口是 <see cref="ResolveAsync"/>，用后立即 Dispose。
/// </para>
/// </remarks>
public sealed class SshCredentialService
{
    private readonly SshCredentialLibrary _library;
    private readonly DpapiCredentialVault _vault;
    private readonly ILogger _logger;

    /// <summary>旧格式凭据文件路径；为 null 表示禁用迁移（测试用）。</summary>
    private readonly string? _legacyPath;

    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private bool _migrated;

    public SshCredentialService(
        SshCredentialLibrary library,
        DpapiCredentialVault vault,
        string? legacyCredentialsPath = null,
        ILogger? logger = null)
    {
        _library = library;
        _vault = vault;
        _legacyPath = legacyCredentialsPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>全部凭据，按名称排序。</summary>
    public async Task<IReadOnlyList<SshCredential>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync();
        return await _library.ListAsync(ct);
    }

    public async Task<SshCredential?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();
        return await _library.GetAsync(id, ct);
    }

    /// <summary>名称是否已被占用（忽略大小写）。<paramref name="excludeId"/> 用于编辑时排除自身。</summary>
    public async Task<bool> IsNameTakenAsync(
        string? name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var all = await GetAllAsync(ct);
        return all.Any(c =>
            c.Id != excludeId &&
            string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "最近使用"的凭据 Id。指向已删除记录的陈旧 id 一律读作 <c>null</c>，
    /// 让调用方回退到列表首项，而不是拿着一个死主键去预选。
    /// </summary>
    public async Task<Guid?> GetLastUsedIdAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync();

        var id = await _library.GetLastUsedIdAsync(ct);
        if (id is null)
        {
            return null;
        }

        return await _library.GetAsync(id.Value, ct) is null ? null : id;
    }

    /// <summary>记录"最近使用"。只接受库里真实存在的 id。</summary>
    public async Task MarkUsedAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();

        if (await _library.GetAsync(id, ct) is null)
        {
            return;
        }

        await _library.SetLastUsedIdAsync(id, ct);
    }

    /// <summary>
    /// 这台虚拟机上次用的凭据 Id；没有记录、或记录已失效时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetLastUsedIdAsync"/> 是<b>两级回退</b>：按 VM 记住了就用它，
    /// 没有（或记的那条已被删）再退回全局最近使用。调用方按这个顺序取。
    /// </remarks>
    public async Task<Guid?> GetDefaultCredentialIdForVmAsync(
        string vmResourceId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();
        return await _library.GetDefaultCredentialIdAsync(vmResourceId, ct);
    }

    /// <summary>
    /// 记住"这台虚拟机用这条凭据"；<paramref name="credentialId"/> 传 <c>null</c> 表示清除记录。
    /// </summary>
    /// <remarks>
    /// 只接受库里真实存在的 id —— 记住一个不存在的主键，下次打开连接框就会拿着死主键去预选，
    /// 而"预选停在一个无关选项上"这种事在界面上很难看出原因。
    /// </remarks>
    public async Task SetDefaultCredentialIdForVmAsync(
        string vmResourceId, Guid? credentialId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();

        if (credentialId is { } id && await _library.GetAsync(id, ct) is null)
        {
            return;
        }

        await _library.SetDefaultCredentialIdAsync(vmResourceId, credentialId, ct);
    }

    /// <summary>有多少台虚拟机记住了这条凭据（删除前的影响提示用，只告知不阻止）。</summary>
    public async Task<int> CountVmsUsingCredentialAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();
        return await _library.CountVmsUsingAsync(id, ct);
    }

    /// <summary>
    /// 新建凭据。<paramref name="secret"/> 在密码方式下是密码（必填）、
    /// 在私钥方式下是 passphrase（可空）；<paramref name="privateKeyBody"/> 是私钥正文。
    /// </summary>
    public async Task<SshCredential> CreateAsync(
        SshCredential draft,
        string? secret,
        string? privateKeyBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await EnsureMigratedAsync();

        var name = (draft.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("凭据名称不能为空。", nameof(draft));
        }

        var username = (draft.Username ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            throw new ArgumentException("用户名不能为空。", nameof(draft));
        }

        // 新建时没有可"保持"的旧值，所以这两种情况都必须给出秘密本身。
        // 一条连不上的凭据不该被存进库里 —— 那正是这次改造要消灭的东西。
        if (draft.AuthType == SshAuthType.Password && string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("密码方式必须提供密码。", nameof(secret));
        }

        if (draft.AuthType == SshAuthType.PrivateKey && string.IsNullOrEmpty(privateKeyBody))
        {
            throw new ArgumentException("私钥方式必须提供私钥内容。", nameof(privateKeyBody));
        }

        if (draft.Id == Guid.Empty)
        {
            draft.Id = Guid.NewGuid();
        }

        var now = DateTimeOffset.Now;
        if (draft.CreatedAt == default)
        {
            draft.CreatedAt = now;
        }

        draft.UpdatedAt = now;

        // 本调用里真正写进保险库的引用键，失败回滚只删这些 —— 绝不误删既有密文
        var created = new List<string>();

        var (secretRef, _) = await StageSecretAsync(
            PurposeFor(draft.AuthType), null, null, secret, created, ct);

        string? keyRef = null;
        if (draft.AuthType == SshAuthType.PrivateKey)
        {
            (keyRef, _) = await StageSecretAsync("sshkey", null, null, privateKeyBody, created, ct);
        }

        draft.Name = name;
        draft.Username = username;
        draft.SecretReference = secretRef;
        draft.KeyReference = keyRef;
        if (draft.AuthType == SshAuthType.Password)
        {
            draft.PrivateKeyPath = null;
        }

        try
        {
            await _library.SaveAsync(draft, ct);   // ← 提交点
        }
        catch
        {
            // 提交失败：把刚落下的密文清掉，不留无人引用的孤儿
            await TryDeleteAsync(created, ct);
            throw;
        }

        return draft;
    }

    /// <summary>
    /// 更新凭据。<paramref name="secret"/> / <paramref name="privateKeyBody"/> 的语义：
    /// <c>null</c> = 保持原值不变；空串 = 清除；非空 = 替换。
    /// </summary>
    /// <remarks>
    /// 认证方式可以从入参改变。<b>切换认证方式必然清理对侧的引用</b>，
    /// 否则会留下"密码方式却带 KeyReference"的脏记录 —— 读时归一化还会再兜一次，
    /// 但脏数据不该被生产出来。
    /// </remarks>
    public async Task<SshCredential> UpdateAsync(
        SshCredential credential,
        string? secret,
        string? privateKeyBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await EnsureMigratedAsync();

        // 以库里现有的记录为准，不信入参里的引用字段 —— 入参可能是个只填了界面字段的副本
        var existing = await _library.GetAsync(credential.Id, ct)
            ?? throw new InvalidOperationException(
                $"凭据不存在（Id={credential.Id}）。它可能已在别处被删除，请刷新后重试。");

        var name = (credential.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("凭据名称不能为空。", nameof(credential));
        }

        var username = (credential.Username ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            throw new ArgumentException("用户名不能为空。", nameof(credential));
        }

        // 同类型编辑时"保持"才有意义；跨类型切换时旧槽位必须作废
        var sameAuthType = existing.AuthType == credential.AuthType;
        var secretBase = sameAuthType ? existing.SecretReference : null;
        var created = new List<string>();
        var stale = new List<string?>();

        var (secretRef, staleSecret) = await StageSecretAsync(
            PurposeFor(credential.AuthType), secretBase, existing.SecretReference, secret, created, ct);
        stale.Add(staleSecret);

        string? keyRef;
        if (credential.AuthType == SshAuthType.PrivateKey)
        {
            var (staged, staleKey) = await StageSecretAsync(
                "sshkey", existing.KeyReference, existing.KeyReference, privateKeyBody, created, ct);
            keyRef = staged;
            stale.Add(staleKey);
        }
        else
        {
            keyRef = null;
            stale.Add(existing.KeyReference);
        }

        // 结果校验：两种方式都必须凑齐建连所需的秘密
        if (credential.AuthType == SshAuthType.Password && secretRef is null)
        {
            await TryDeleteAsync(created, ct);
            throw new ArgumentException("密码方式必须提供密码。", nameof(secret));
        }

        if (credential.AuthType == SshAuthType.PrivateKey && keyRef is null)
        {
            await TryDeleteAsync(created, ct);
            throw new ArgumentException(
                "私钥方式必须提供私钥内容（该凭据当前没有已保存的私钥）。", nameof(privateKeyBody));
        }

        var updated = new SshCredential
        {
            Id = existing.Id,
            Name = name,
            Username = username,
            AuthType = credential.AuthType,
            SecretReference = secretRef,
            KeyReference = keyRef,
            PrivateKeyPath = credential.AuthType == SshAuthType.PrivateKey
                ? credential.PrivateKeyPath
                : null,
            Description = credential.Description ?? string.Empty,
            CreatedAt = existing.CreatedAt,     // 创建时间不可被编辑改动
            UpdatedAt = DateTimeOffset.Now,
        };

        try
        {
            await _library.SaveAsync(updated, ct);   // ← 提交点
        }
        catch
        {
            await TryDeleteAsync(created, ct);
            throw;
        }

        // 提交之后才清理旧密文。失败只记日志 —— 最多留下孤儿密文，
        // 而反过来（先删旧再提交）失败会留下"看得见却连不上"的幽灵凭据。
        await TryDeleteAsync(stale, ct);

        return updated;
    }

    /// <summary>
    /// 删除凭据：元数据与保险库里的密文一并清除。
    /// </summary>
    /// <remarks>
    /// 提交点在<b>前</b>：先删元数据，凭据随即从列表与下拉里消失，
    /// 不存在"删一半、留下个连不上的可见条目"的中间态。
    /// 不存在的 id 视为已达成目标，静默返回（幂等）。
    /// 已建立的终端会话不受影响 —— 它们的明文早已在内存里，这里不做任何会话联动。
    /// </remarks>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();

        var removed = await _library.DeleteAsync(id, ct);   // ← 提交点
        if (removed is null)
        {
            return;
        }

        await TryDeleteAsync([removed.SecretReference, removed.KeyReference], ct);
    }

    /// <summary>
    /// 解出明文凭据供建连使用。返回的对象<b>生命周期极短</b>，调用方必须 <c>using</c>。
    /// <para>
    /// 无法解出（记录陈旧、或保险库来自其他 Windows 账户 / 机器）时返回 <c>null</c>，
    /// <b>不抛异常</b>：调用方要能把它转成一句有用的界面提示，而不是一个红框。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 认证方式一律由 <see cref="SshCredential.AuthType"/> 决定，
    /// <b>绝不靠"哪个密文解出来了"反推</b>。后者在记录脏掉时会让用户拿着密码去连
    /// 只认私钥的主机，而失败提示还是"认证失败：请检查用户名、密码"—— 排查方向被彻底带偏。
    /// </remarks>
    public async Task<ResolvedSshCredential?> ResolveAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync();

        var credential = await _library.GetAsync(id, ct);
        if (credential is null)
        {
            return null;
        }

        if (credential.AuthType == SshAuthType.Password)
        {
            var password = await RetrieveAsync(credential.SecretReference, ct);
            if (password is null)
            {
                LogUnresolvable(credential);
                return null;
            }

            return new ResolvedSshCredential(credential.AuthType, credential.Username, password, null, null);
        }

        var privateKey = await RetrieveAsync(credential.KeyReference, ct);
        if (privateKey is null)
        {
            LogUnresolvable(credential);
            return null;
        }

        var passphrase = await RetrieveAsync(credential.SecretReference, ct);
        return new ResolvedSshCredential(
            credential.AuthType, credential.Username, null, privateKey, passphrase);
    }

    // ── 内部 ──────────────────────────────────────────────────────

    /// <summary>密码方式用 <c>ssh</c>、私钥方式用 <c>sshpass</c>（该槽位放的是 passphrase）。</summary>
    private static string PurposeFor(SshAuthType authType) =>
        authType == SshAuthType.Password ? "ssh" : "sshpass";

    /// <summary>
    /// 秘密槽位的暂存：把新密文写进保险库并返回该槽位<b>提交后</b>应有的引用键。
    /// </summary>
    /// <param name="keepReference">值为 <c>null</c>（保持）时沿用哪个引用。</param>
    /// <param name="currentReference">库里当前真实的引用，即"要清理的旧密文"。</param>
    /// <param name="value"><c>null</c> = 保持；空串 = 清除；非空 = 替换为新密文。</param>
    /// <param name="created">本调用真正新建的引用键，供失败回滚精确删除。</param>
    /// <returns>提交后应写入元数据的引用键，以及提交后应清理的旧引用键。</returns>
    private async Task<(string? Reference, string? Stale)> StageSecretAsync(
        string purpose,
        string? keepReference,
        string? currentReference,
        string? value,
        List<string> created,
        CancellationToken ct)
    {
        if (value is null)
        {
            // 保持。跨认证方式切换时 keepReference 为 null，于是旧引用落入 Stale 被清掉。
            return keepReference == currentReference
                ? (keepReference, null)
                : (keepReference, currentReference);
        }

        if (value.Length == 0)
        {
            return (null, currentReference);
        }

        var reference = DpapiCredentialVault.CreateReference(purpose);
        await _vault.StoreSecretAsync(reference, value, ct);
        created.Add(reference);
        return (reference, currentReference);
    }

    private async Task<string?> RetrieveAsync(string? reference, CancellationToken ct) =>
        string.IsNullOrEmpty(reference) ? null : await _vault.RetrieveSecretAsync(reference, ct);

    /// <summary>
    /// 尽力清理保险库里的密文。失败只记日志 —— 清理是"提交之后"的收尾动作，
    /// 让它把一次已经成功的删除/更新变成报错，是拿主流程去赔一个孤儿密文。
    /// </summary>
    private async Task TryDeleteAsync(IEnumerable<string?> references, CancellationToken ct)
    {
        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference))
            {
                continue;
            }

            try
            {
                await _vault.DeleteSecretAsync(reference, ct);
            }
            catch (Exception ex)
            {
                // 只有引用键，没有任何明文
                _logger.LogWarning(ex, "清理凭据保险库中的旧密文失败，引用键 {Reference}", reference);
            }
        }
    }

    /// <summary>只记名字与事实，绝不记密文或明文。</summary>
    private void LogUnresolvable(SshCredential credential) =>
        _logger.LogWarning(
            "凭据 {Name} 的密文无法解出，该凭据暂不可用（保险库文件可能来自其他 Windows 账户或机器）",
            credential.Name);

    /// <summary>
    /// 首次使用前把旧格式迁移一次。成功才置位，失败下次重试 ——
    /// 不缓存一个已 faulted 的 Task，否则一次瞬时写盘失败会让本次进程里凭据功能全程不可用。
    /// </summary>
    private async Task EnsureMigratedAsync()
    {
        if (_legacyPath is null || _migrated)
        {
            return;
        }

        await _migrationGate.WaitAsync();
        try
        {
            if (_migrated)
            {
                return;
            }

            // 迁移不跟随调用方的 ct：某个对话框被取消不该让迁移半途而废
            await _library.MigrateFromLegacyAsync(_legacyPath, CancellationToken.None);
            _migrated = true;
        }
        finally
        {
            _migrationGate.Release();
        }
    }
}
