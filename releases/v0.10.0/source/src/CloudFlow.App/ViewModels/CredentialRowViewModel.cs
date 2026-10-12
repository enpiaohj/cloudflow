using CloudFlow.Terminal.Security;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 设置页「凭据管理」列表里的一行。
/// </summary>
/// <remarks>
/// 刻意是普通类而不是 <c>ObservableObject</c>：列表每次刷新都整体替换
/// （<c>SettingsViewModel.Credentials = [...]</c>），单行不存在"就地变更"的场景，
/// 加一套通知机制只会多出一段永远不被触发的代码。
/// </remarks>
public sealed class CredentialRowViewModel
{
    public required SshCredential Credential { get; init; }

    public Guid Id => Credential.Id;

    public string Name => Credential.Name;

    public string Username => Credential.Username;

    public string Description => Credential.Description;

    /// <summary>认证方式的中文名，供徽章显示。</summary>
    public string AuthTypeText => Credential.AuthType == SshAuthType.Password ? "密码" : "私钥";

    /// <summary>
    /// 徽章配色键（英文枚举，见 CfStatusBrushConverter）。
    /// 这是<b>分类</b>不是评价 —— 两者都不是"好"或"坏"，
    /// 所以刻意避开 Success / Danger 这两档语义色。
    /// </summary>
    public string AuthTypeKey => Credential.AuthType == SshAuthType.Password ? "Info" : "Subnet";

    /// <summary>
    /// 元数据缺失秘密时的警告文案；正常情况为 <c>null</c>（界面据此收起整行）。
    /// </summary>
    /// <remarks>
    /// 只反映<b>引用键在不在</b>，不代表密文真能解开 —— 保险库文件若来自其他 Windows 账户，
    /// 引用键完好但解密必然失败。那一层只有建连时才知道，所以这里绝不写"可用"。
    /// </remarks>
    public string? MissingSecretText => Credential.HasSecretReference
        ? null
        : Credential.AuthType == SshAuthType.Password ? "未保存密码" : "未保存私钥内容";

    public override string ToString() => $"CredentialRowViewModel({Name}, {AuthTypeText})";
}
