namespace CloudFlow.Core.Identity;

/// <summary>
/// 身份 Provider 类型（P0 Spike，规范 §三/§十五）：
/// - EntraMsal：Microsoft Entra 工作/学校账户（MSAL + WAM/系统浏览器）
/// - EmbeddedAzureCli：Personal Microsoft Account 关联 Azure 订阅（CloudFlow 托管 Azure CLI Bridge）
/// </summary>
public enum AuthenticationProviderType
{
    EntraMsal,
    EmbeddedAzureCli
}
