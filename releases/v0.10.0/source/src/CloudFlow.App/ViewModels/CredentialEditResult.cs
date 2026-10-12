using CloudFlow.Terminal.Security;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 凭据编辑对话框的一次结果：元数据 + 明文秘密，由设置页转交给
/// <see cref="SshCredentialService"/> 落库。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Secret"/> / <see cref="PrivateKeyBody"/> 沿用服务层的三态语义：
/// <c>null</c> = 保持原值不变；空串 = 清除；非空 = 替换为这个新值。
/// </para>
/// <para>
/// <b>本对象持有明文，生命周期只到那次调用为止</b>：对话框在交出它之前已
/// <c>PasswordBox.Clear()</c>，调用方用完即不再引用。绝不写日志、不进异常消息、
/// 不参与序列化 —— 因此 <c>ToString()</c> 必须重写（见下）。
/// </para>
/// </remarks>
public sealed class CredentialEditResult
{
    public required SshCredential Credential { get; init; }

    /// <summary>密码（密码方式）或 passphrase（私钥方式）的明文。见类型注释的三态语义。</summary>
    public required string? Secret { get; init; }

    /// <summary>私钥<b>正文</b>（私钥方式）。<c>null</c> = 保持保险库里已存的不变。</summary>
    public required string? PrivateKeyBody { get; init; }

    /// <summary>只打印名称与认证方式 —— 明文字段一个都不出现。</summary>
    public override string ToString() =>
        $"CredentialEditResult({Credential.Name}, {Credential.AuthType})";
}
