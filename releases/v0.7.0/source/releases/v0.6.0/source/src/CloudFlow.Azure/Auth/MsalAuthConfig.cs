using System.Text.RegularExpressions;

namespace CloudFlow.Azure.Auth;

/// <summary>
/// MSAL 认证配置（来自 appsettings.json 的 "Azure" 节）。
/// Public Client，无 Client Secret（桌面应用，设计文档 §69）。
/// </summary>
public sealed partial class MsalAuthConfig
{
    public const string SectionName = "Azure";

    /// <summary>
    /// 本机用户配置文件名。位于 %LOCALAPPDATA%\CloudFlow\，由「设置 → 账户 → 登录服务」写入
    /// （见 <see cref="MsalAuthConfigFile"/>）；启动时在程序目录的 appsettings.json 之后读取，优先级更高。
    /// 单文件发布包里没有程序目录那一份，这里是正式版唯一的配置来源。
    /// </summary>
    public const string UserConfigFileName = "appsettings.json";

    /// <summary>Authority 实例，默认 https://login.microsoftonline.com/。</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Azure Resource Manager 只支持 Entra 组织账户的委托管理上下文。</summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>
    /// App Registration 的 Client ID <b>占位值</b>。
    /// </summary>
    /// <remarks>
    /// <b>这里刻意只放占位符，不放任何真实的 App Registration ID。</b>
    /// 真实值属于本机环境配置：开发时放在被 gitignore 的 <c>appsettings.json</c> 里
    /// （模板见同目录的 <c>appsettings.example.json</c>），正式版由用户在设置页填写。
    /// <para>
    /// <see cref="IsConfigured"/> 认这个前缀，因此未配置时会明确判为"未配置"，
    /// 而不是拿着一个假 ID 去登录、再返回一个看不懂的 AADSTS 错误。
    /// </para>
    /// </remarks>
    public const string DefaultClientId = "SET-YOUR-PUBLIC-CLIENT-ID";

    /// <summary>App Registration 的 Client ID（Public Client）。</summary>
    public string ClientId { get; set; } = DefaultClientId;

    /// <summary>
    /// 交互登录 Redirect URI。桌面 Public Client 固定 http://localhost（Entra 对该前缀允许任意端口），
    /// 配合系统浏览器完成登录。不要用 WithDefaultRedirectUri()：在 net8.0-windows TFM 下
    /// 会解析出需要 WebView2 的重定向类型，WebView2 不可用时登录直接失败。
    /// </summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>ARM API 所需 Scope。</summary>
    public string ManagementScope { get; set; } = "https://management.azure.com/.default";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !ClientId.StartsWith("SET-YOUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>应用（客户端）ID：Entra 应用注册的 Application (client) ID 是非空 GUID。</summary>
    public static bool IsValidClientId(string? value) =>
        Guid.TryParse(value?.Trim(), out var id) && id != Guid.Empty;

    /// <summary>
    /// 目录（租户）：<c>organizations</c>（多租户应用）、<c>common</c>、租户 ID（GUID）或已验证域名。
    /// 不接受 <c>consumers</c>：ARM 只支持 Entra 组织账户，个人 Microsoft 账户走嵌入式 Azure CLI。
    /// </summary>
    public static bool IsValidTenant(string? value)
    {
        var tenant = value?.Trim() ?? "";
        if (tenant.Length == 0)
        {
            return false;
        }

        if (tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase) ||
            tenant.Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Guid.TryParse(tenant, out var id))
        {
            return id != Guid.Empty;
        }

        return DomainPattern().IsMatch(tenant);
    }

    [GeneratedRegex(@"^(?=.{4,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$")]
    private static partial Regex DomainPattern();
}
