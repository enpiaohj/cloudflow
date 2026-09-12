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
