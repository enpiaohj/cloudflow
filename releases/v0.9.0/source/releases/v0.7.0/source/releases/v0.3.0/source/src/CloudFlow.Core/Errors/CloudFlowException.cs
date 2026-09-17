namespace CloudFlow.Core.Errors;

public enum CloudFlowErrorCode
{
    Unknown,

    /// <summary>App Registration 等平台配置缺失。</summary>
    NotConfigured,

    /// <summary>操作预校验失败（Validate 阶段）。</summary>
    ValidationFailed,

    ResourceNotFound,

    AzureError,

    /// <summary>Verify 阶段结果与预期不符。</summary>
    VerificationFailed,

    /// <summary>登录会话失效（Refresh Token 撤销/过期），需要用户重新登录。</summary>
    ReauthenticationRequired,

    /// <summary>该路径不支持此能力（如数据面被要求直接执行写操作，绕过 Operation Engine）。</summary>
    NotSupported,

    Canceled
}

/// <summary>CloudFlow 统一异常基类。</summary>
public class CloudFlowException : Exception
{
    public CloudFlowErrorCode Code { get; }

    public CloudFlowException(CloudFlowErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>平台配置缺失（如未配置 App Registration）。</summary>
public sealed class NotConfiguredException(string message) : CloudFlowException(CloudFlowErrorCode.NotConfigured, message);

/// <summary>操作校验失败。</summary>
public sealed class OperationValidationException(string message) : CloudFlowException(CloudFlowErrorCode.ValidationFailed, message);

/// <summary>
/// 登录会话失效，需要用户到「设置 → 使用 Microsoft 登录」重新交互登录。
/// 认证层绝不自行弹出登录窗（避免数据查询中途突兀弹浏览器）。
/// </summary>
public sealed class ReauthenticationRequiredException(string message, Exception? inner = null)
    : CloudFlowException(CloudFlowErrorCode.ReauthenticationRequired, message, inner);
