namespace CloudFlow.Azure.Auth;

/// <summary>
/// MSAL 认证配置（来自 appsettings.json 的 "Azure" 节）。
/// Public Client，无 Client Secret（桌面应用，设计文档 §69）。
/// </summary>
public sealed class MsalAuthConfig
{
    public const string SectionName = "Azure";

    /// <summary>Authority 实例，默认 https://login.microsoftonline.com/。</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// Tenant：多租户应用用 "organizations"，单租户用具体 Tenant ID。
    /// </summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>App Registration 的 Client ID（Public Client）。</summary>
    public string ClientId { get; set; } = "";

    /// <summary>ARM API 所需 Scope。</summary>
    public string ManagementScope { get; set; } = "https://management.azure.com/.default";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !ClientId.StartsWith("SET-YOUR", StringComparison.OrdinalIgnoreCase);
}
