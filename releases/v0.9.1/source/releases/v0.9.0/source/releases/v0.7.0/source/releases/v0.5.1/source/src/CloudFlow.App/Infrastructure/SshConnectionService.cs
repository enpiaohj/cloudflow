using System.IO;
using CloudFlow.Terminal.Security;
using CloudFlow.Terminal.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudFlow.App.Infrastructure;

/// <summary>SSH 认证方式（凭据对话框选项）。</summary>
public enum SshCredentialAuthType
{
    Password,
    PrivateKeyFile
}

/// <summary>
/// 凭据对话框的一次输入。两条互斥的出口：
/// <list type="bullet">
/// <item><see cref="UseCredentialId"/> 非 null —— 使用凭据库里那一条，其余凭据字段一律忽略；</item>
/// <item><see cref="UseCredentialId"/> 为 null —— 用本次输入的字段临时连接，
/// <see cref="SaveCredential"/> 为 true 时另外存入凭据库。</item>
/// </list>
/// </summary>
/// <remarks>
/// 刻意是 <c>class</c> 而不是 <c>record</c>：record 由编译器生成的 <c>ToString()</c>
/// 会把 <see cref="Password"/> 与 <see cref="Passphrase"/> 原样打印出来 ——
/// 一次 <c>$"{input}"</c>、一句调试器展开、一个被格式化的日志参数就足以把明文密码写进日志，
/// 与「日志 / 审计 / 异常消息 / <c>ToString()</c> 一律不得含 Secret」的硬约束直接冲突。
/// 本类重写 <c>ToString()</c>，只输出非敏感字段。<b>不要改回 record。</b>
/// </remarks>
public sealed class SshCredentialInput
{
    public required string Username { get; init; }

    public SshCredentialAuthType AuthType { get; init; }

    public string? Password { get; init; }

    /// <summary>私钥文件路径（PrivateKeyFile 方式）；内容在保存 / 连接时才读入。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>私钥 passphrase（PrivateKeyFile 方式）。</summary>
    public string? Passphrase { get; init; }

    /// <summary>
    /// 非 <c>null</c> 表示<b>使用凭据库里这一条</b>：由调用方从保险库解密，
    /// 上面那些凭据字段只是对话框里的残留值，一律忽略。
    /// </summary>
    public Guid? UseCredentialId { get; init; }

    /// <summary>勾选「存入凭据库」时必填的名称。</summary>
    public string? Name { get; init; }

    /// <summary>是否把本次输入存入全局凭据库。仅当 <see cref="UseCredentialId"/> 为 null 时有意义。</summary>
    public bool SaveCredential { get; init; }

    /// <summary>只打印非秘密字段 —— 这是它能被安全地交给日志与调试器的前提。</summary>
    public override string ToString() =>
        $"SshCredentialInput({Username}, {AuthType}, SaveCredential={SaveCredential}, UseCredentialId={UseCredentialId})";
}

/// <summary>
/// SSH 连接的凭据 / 指纹协调者（App 层门面）。
/// 明文 Secret 只在两个位置出现：对话框输入框、<see cref="DpapiCredentialVault"/> 内部；
/// 其余任何地方（日志 / 审计 / 序列化）一律不得出现。
/// </summary>
/// <remarks>
/// <b>本类不再持有任何"每 VM 记住凭据"的存储</b>：那条路径已由全局凭据库取代，
/// 旧的 <c>ssh-credentials.json</c> 只剩两个用途 —— 迁移源（见下）与用户自己的回滚依据。
/// </remarks>
public sealed class SshConnectionService
{
    public KnownHostStore KnownHosts { get; }

    /// <summary>
    /// 全局凭据库 —— 设置页「凭据管理」与连接对话框下拉的唯一数据源。
    /// </summary>
    /// <remarks>
    /// 挂在本类（Singleton）上，而不是另注册一个 Singleton 进 DI：
    /// <see cref="DpapiCredentialVault"/> 的写入互斥是一把<b>进程内</b> <c>SemaphoreSlim</c>，
    /// 两个实例各持一把锁就等于没有任何互斥。由本类独家持有这个 vault，
    /// 才能保证保险库文件在一个进程里只有一个写入者。
    /// </remarks>
    public SshCredentialService CredentialLibrary { get; }

    public SshConnectionService(ILogger<SshConnectionService>? logger = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudFlow");

        // 保险库与凭据库的告警（密文解不开、旧引用被归一化丢弃）必须真的能落地：
        // 用 NullLogger 的话，"凭据连不上"在日志里就完全无迹可寻。
        // 这三个类都不记明文，只记名称与引用键。
        var log = (ILogger?)logger ?? NullLogger.Instance;

        KnownHosts = new KnownHostStore(Path.Combine(root, "known-hosts.json"));

        // vault 只交给凭据库持有，本类不留字段 —— 它需要是唯一的写入者
        var vault = new DpapiCredentialVault(Path.Combine(root, "ssh-vault.dat"), log);

        // 旧文件路径同时是迁移源：凭据服务首次使用时把 ssh-credentials.json 里的
        // 每 VM 记录搬进全局库。旧文件全程只读、不删、迁移后不再被读取。
        CredentialLibrary = new SshCredentialService(
            new SshCredentialLibrary(Path.Combine(root, "ssh-credentials-library.json"), log),
            vault,
            legacyCredentialsPath: Path.Combine(root, "ssh-credentials.json"),
            logger: log);
    }

    /// <summary>
    /// 用凭据库里的某条凭据构建连接参数（从保险库解密，明文只存活到断开）。
    /// 解不出时返回 <c>null</c>，由调用方决定如何提示。
    /// </summary>
    /// <remarks>
    /// 只接收 Id 而不是实体：库里那条记录才是唯一事实来源，
    /// 传一个可能是几分钟前加载的陈旧副本进来，会让"改过密码"这类情况连错。
    /// </remarks>
    public async Task<SshConnectionOptions?> BuildOptionsFromCredentialAsync(
        Guid credentialId, string host, int port, SshHostKeyPolicy policy)
    {
        // ResolveAsync 的返回值持有明文，生命周期极短。Dispose 只把它的属性置 null，
        // 而 string 是不可变引用 —— 拷进 options 的引用在 Dispose 之后依然有效。
        using var resolved = await CredentialLibrary.ResolveAsync(credentialId);
        if (resolved is null)
        {
            return null;
        }

        return new SshConnectionOptions
        {
            Host = host,
            Port = port,
            Username = resolved.Username,
            AuthType = resolved.AuthType,
            Password = resolved.Password,
            PrivateKey = resolved.PrivateKey,
            // passphrase 必须带上：漏掉它，带 passphrase 的私钥凭据就是"存了也连不上"
            Passphrase = resolved.Passphrase,
            HostKeyPolicy = policy
        };
    }

    /// <summary>
    /// 用对话框里临时输入的凭据构建连接参数。
    /// </summary>
    /// <remarks>
    /// <b>本方法不落盘、不入库</b> —— 需要持久化的输入由调用方走
    /// <see cref="SshCredentialService.CreateAsync"/>。
    /// 把"写库"留在这里会让这个方法既是纯函数又是写入者，调用方无法判断失败发生在哪一步。
    /// </remarks>
    public async Task<SshConnectionOptions> BuildTransientOptionsAsync(
        SshCredentialInput input, string host, int port, SshHostKeyPolicy policy)
    {
        string? privateKey = null;
        if (input.AuthType == SshCredentialAuthType.PrivateKeyFile)
        {
            privateKey = await File.ReadAllTextAsync(input.PrivateKeyPath!);
        }

        return new SshConnectionOptions
        {
            Host = host,
            Port = port,
            Username = input.Username,
            AuthType = input.AuthType == SshCredentialAuthType.Password
                ? SshAuthType.Password
                : SshAuthType.PrivateKey,
            // 认证方式为私钥时不带密码：隐藏的密码框里可能还留着用户切换前敲的内容，
            // 把它带进一个只认私钥的连接里，只会让失败原因更难判断。
            Password = input.AuthType == SshCredentialAuthType.Password ? input.Password : null,
            PrivateKey = privateKey,
            Passphrase = input.AuthType == SshCredentialAuthType.PrivateKeyFile ? input.Passphrase : null,
            HostKeyPolicy = policy
        };
    }
}
