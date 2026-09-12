using CloudFlow.Core.Identity;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// Azure Client 工厂平台接口（设计文档 §71）。
/// 任何 Module 不直接 new ArmClient(...)，统一经由工厂按 AccountSession + Tenant 创建，
/// 保证跨 Account / Tenant 操作不会混用 Token。
/// </summary>
public interface IAzureClientFactory
{
    /// <summary>为指定会话创建 ARM Client（Token 来自 MSAL，经 TokenCredential 桥接）。</summary>
    object CreateArmClient(AccountSession session, string? tenantId = null);
}

/// <summary>
/// AzureClientFactory 的 MSAL 版本。
/// P1 实现：MsalTokenCredential（TokenCredential 适配 MSAL AccountSession）+ ArmClient。
/// 当前阶段仅保留接口与占位，等待 appsettings 配置真实 ClientId 后接入 Azure.ResourceManager。
/// </summary>
public sealed class MsalAzureClientFactory : IAzureClientFactory
{
    public object CreateArmClient(AccountSession session, string? tenantId = null)
    {
        throw new NotImplementedException(
            "ARM Client 将在 MSAL 登录验证（P0 Spike）通过后接入 Azure.ResourceManager。");
    }
}
