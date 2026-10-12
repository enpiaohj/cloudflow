using System.Net.Sockets;
using CloudFlow.Terminal.Security;

namespace CloudFlow.Terminal.Ssh;

/// <summary>
/// 建立 SSH 连接所需的全部参数。凭据字段（Password/PrivateKey/Passphrase）是短生命周期内存对象：
/// 只在连接期间存活，断开即清，绝不落盘、绝不进日志。
/// </summary>
public sealed class SshConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }

    public SshAuthType AuthType { get; init; } = SshAuthType.PrivateKey;

    /// <summary>密码方式：登录密码。</summary>
    public string? Password { get; init; }

    /// <summary>私钥方式：PEM 内容（不是文件路径）。</summary>
    public string? PrivateKey { get; init; }

    /// <summary>私钥 passphrase，可空。</summary>
    public string? Passphrase { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 15;

    public required SshHostKeyPolicy HostKeyPolicy { get; init; }
}

/// <summary>会话状态机：Idle → Connecting → (Connected → Disconnected | Failed) → Closed。</summary>
public enum SshSessionState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

/// <summary>标准化连接错误码。UI 只面对错误码与中文消息，不面对底层异常。</summary>
public enum SshConnectionErrorCode
{
    None,
    CredentialMissing,
    AuthenticationFailed,
    HostNotFound,
    Timeout,
    NetworkUnreachable,
    RemoteClosed,
    ProtocolNegotiationFailed,
    HostKeyRejected,
    HostKeyMismatch,
    Cancelled,
    Unknown
}

public static class SshConnectionErrorText
{
    /// <summary>错误码 → 中文提示（终端状态条直接展示）。</summary>
    public static string Describe(SshConnectionErrorCode code) => code switch
    {
        SshConnectionErrorCode.CredentialMissing => "缺少凭据：请提供密码或私钥。",
        SshConnectionErrorCode.AuthenticationFailed => "认证失败：请检查用户名、密码或私钥是否正确。",
        SshConnectionErrorCode.HostNotFound => "主机名无法解析，请检查 IP 地址。",
        SshConnectionErrorCode.Timeout => "连接超时：主机无响应（检查 IP、端口与网络安全规则）。",
        SshConnectionErrorCode.NetworkUnreachable => "网络不可达。",
        SshConnectionErrorCode.RemoteClosed => "连接被远端关闭。",
        SshConnectionErrorCode.ProtocolNegotiationFailed => "SSH 协议协商失败。",
        SshConnectionErrorCode.HostKeyRejected => "主机密钥被拒绝，连接已中止。",
        SshConnectionErrorCode.HostKeyMismatch => "主机密钥与已记录的不一致，连接已中止（可能存在中间人风险）。",
        SshConnectionErrorCode.Cancelled => "连接已取消。",
        _ => "发生未知错误。"
    };

    /// <summary>底层异常 → 标准化错误码（RemoteFlow MapError 同款映射）。</summary>
    public static SshConnectionErrorCode Map(Exception ex, SshConnectionErrorCode hostKeyFailure)
    {
        // Host Key 校验失败会以 SshConnectionException 冒泡，优先采用校验阶段记录的精确原因。
        if (hostKeyFailure != SshConnectionErrorCode.None)
        {
            return hostKeyFailure;
        }

        return ex switch
        {
            Renci.SshNet.Common.SshAuthenticationException => SshConnectionErrorCode.AuthenticationFailed,
            System.Net.Sockets.SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
                => SshConnectionErrorCode.HostNotFound,
            System.Net.Sockets.SocketException { SocketErrorCode: SocketError.TimedOut }
                => SshConnectionErrorCode.Timeout,
            System.Net.Sockets.SocketException => SshConnectionErrorCode.NetworkUnreachable,
            Renci.SshNet.Common.SshOperationTimeoutException => SshConnectionErrorCode.Timeout,
            Renci.SshNet.Common.SshConnectionException => SshConnectionErrorCode.NetworkUnreachable,
            Renci.SshNet.Common.SshException => SshConnectionErrorCode.ProtocolNegotiationFailed,
            _ => SshConnectionErrorCode.Unknown
        };
    }
}
