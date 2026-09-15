using System.Text.Json.Serialization;

namespace CloudFlow.Terminal.Security;

/// <summary>
/// 全局 SSH 凭据的<b>元数据</b>（ssh-credentials-library.json）。
/// 与主机无关 —— 一条凭据可用于任意 VM，宿主机地址只存在于连接参数里。
/// <para>
/// 只存引用键，<b>永不持有明文 Secret</b>；明文经 DPAPI 加密后放在
/// <see cref="DpapiCredentialVault"/> 里，两者靠引用键关联。
/// </para>
/// </summary>
/// <remarks>
/// 刻意是 <c>class</c> 而不是 <c>record</c>：record 由编译器生成的 <c>ToString()</c>
/// 会原样打印全部属性，一旦将来有人往这里加一个明文字段就会静默泄漏 ——
/// <c>SshConnectionService.SshCredentialInput</c> 原来那个缺陷就是这么来的。
/// 本类重写 <c>ToString()</c>，只输出非敏感字段。<b>不要改回 record。</b>
/// </remarks>
public sealed class SshCredential
{
    /// <summary>稳定主键。与 Azure ResourceId 无关 —— 凭据不挂在资源层级上。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名。服务层强制不区分大小写唯一（重名会让连接框的下拉无法分辨）。</summary>
    public required string Name { get; set; }

    public string Username { get; set; } = "azureuser";

    public SshAuthType AuthType { get; set; } = SshAuthType.PrivateKey;

    /// <summary>
    /// 密码密文引用。认证方式为 <see cref="SshAuthType.PrivateKey"/> 时，它存的是
    /// <b>私钥 passphrase</b>（照 RemoteFlow 的取舍：两种秘密共用这一个槽位，
    /// 于是不需要"哪个引用对应哪种用途"的额外字段）。
    /// <para>
    /// 槽位的含义由 <see cref="AuthType"/> <b>唯一</b>决定 —— 绝不允许反过来靠
    /// "哪个引用非空"去推断认证方式，那会在记录脏掉时拿着密码去连只认私钥的主机。
    /// </para>
    /// </summary>
    public string? SecretReference { get; set; }

    /// <summary>私钥<b>正文</b>的密文引用（仅 PrivateKey 方式）。可空。</summary>
    public string? KeyReference { get; set; }

    /// <summary>
    /// 上次选择私钥文件的位置。非秘密，<b>仅作"浏览…"对话框的起始路径</b>，
    /// 不是连接依据 —— 连接只用 <see cref="KeyReference"/> 里的正文。
    /// 注意它会以明文写进 JSON，可能连带暴露 Windows 用户名。
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>备注。明文保存在本机，编辑框已提示不要写入密码。</summary>
    public string Description { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 元数据是否声称自己持有 Secret。它只反映<b>引用键在不在</b>，不代表密文真能解开
    /// （保险库文件被换成别的 Windows 账户写的就解不开）—— 后者只有解密时才知道。
    /// </summary>
    [JsonIgnore]
    public bool HasSecretReference => SecretReference is not null || KeyReference is not null;

    public override string ToString() => $"SshCredential({Name}, {AuthType}, {Username})";
}

/// <summary>
/// 凭据库文件的根对象。带 schema 版本号，便于将来做破坏性变更时明确识别。
/// </summary>
public sealed class SshCredentialLibraryDocument
{
    /// <summary>
    /// 文件格式版本。
    /// </summary>
    /// <remarks>
    /// <b>v1 → v2</b>：新增 <see cref="DefaultCredentialByVm"/>。这是<b>写入方向不兼容</b>的变更 ——
    /// v1 时代的程序读不懂这个字段，一旦由它保存就会被<b>静默丢掉</b>。
    /// 所以必须升版本号：那个程序读到 v2 会显式抛错（见 <c>SshCredentialLibrary.LoadAsync</c>
    /// 的版本检查），而不是悄悄把用户"每台机器用哪条凭据"的记忆抹掉。
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// 全局最近使用过的凭据 Id —— <b>只有一个标量</b>，不构成任何主机关联，
    /// 只回答"上一次用了哪条"。
    /// </summary>
    /// <remarks>
    /// 放在本文件而不是 <c>AppSettings</c>，是为了让删除凭据能在同一把锁、同一次写入里
    /// 把它一并清掉 —— 跨两个文件提交会留下"预选指向一条已删除凭据"的窗口。
    /// <see cref="DefaultCredentialByVm"/> 出于同样的理由放在这里。
    /// </remarks>
    public Guid? LastUsedCredentialId { get; set; }

    /// <summary>
    /// 每台虚拟机记住的那条凭据（键 = Azure ResourceId，值 = 凭据 Id）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一项改过口径，别照着旧注释理解。</b> v1 时刻意不做 VM → 凭据 映射，
    /// 理由是「凭据是全局资产、不该绑主机」。2026-09-13 用户改了口径：
    /// <b>按 VM 记住，每台各记一条</b> —— 选了凭据点「连接」就记住，
    /// 下次进这台机器的连接框预选它，直到用户改选。起因是"每次都要重新挑一遍"太烦。
    /// </para>
    /// <para>
    /// 它<b>不是</b>"凭据绑定主机"：凭据仍可用于任意 VM，这里只是"这台上次用了哪条"的备忘。
    /// 指向已删除凭据的条目在读取时被判为无效并回退（见 <c>SshCredentialService</c>），
    /// 主动清理则与删除凭据同一次写入完成，不留陈旧项。
    /// </para>
    /// </remarks>
    public Dictionary<string, Guid> DefaultCredentialByVm { get; set; } = [];

    public List<SshCredential> Credentials { get; set; } = [];
}

/// <summary>
/// 已解出明文的凭据，<b>生命周期极短</b>：建连前解析、建连后立即 Dispose。
/// 绝不写入磁盘、日志、异常消息，也不参与序列化。
/// </summary>
/// <remarks>
/// <c>Dispose</c> 只是把引用置空，让明文字符串尽早变成垃圾、缩短它在托管堆上的存活时间。
/// <b>诚实说明</b>：<c>string</c> 不可擦除（它不是字节数组，拿不到 <c>ZeroMemory</c> 这种手段），
/// 所以这是卫生措施而不是安全保证 —— 真正的保证在保险库那一层，
/// 它对明文字节做了 <c>CryptographicOperations.ZeroMemory</c>。
/// </remarks>
public sealed class ResolvedSshCredential : IDisposable
{
    public ResolvedSshCredential(
        SshAuthType authType, string username, string? password, string? privateKey, string? passphrase)
    {
        AuthType = authType;
        Username = username;
        Password = password;
        PrivateKey = privateKey;
        Passphrase = passphrase;
    }

    public SshAuthType AuthType { get; }

    public string Username { get; }

    public string? Password { get; private set; }

    public string? PrivateKey { get; private set; }

    public string? Passphrase { get; private set; }

    public void Dispose()
    {
        Password = null;
        PrivateKey = null;
        Passphrase = null;
    }

    /// <summary>只打印类型与用户名 —— 这是它能在调试器里被安全展开的前提。</summary>
    public override string ToString() => $"ResolvedSshCredential({AuthType}, {Username})";
}
