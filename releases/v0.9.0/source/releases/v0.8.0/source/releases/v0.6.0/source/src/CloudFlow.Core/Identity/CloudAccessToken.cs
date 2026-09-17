namespace CloudFlow.Core.Identity;

/// <summary>
/// Provider 签发的访问令牌能力（仅内存回调）。
/// Token 本身禁止落盘、写日志或进入持久化模型（P0 安全要求 §十）。
/// 平台层（CloudFlow.Azure）负责把它适配为 Azure.SDK TokenCredential。
/// </summary>
public sealed class CloudAccessToken
{
    public required Func<IEnumerable<string>, CancellationToken, Task<string>> AcquireAsync { get; init; }

    public Task<string> GetAsync(IEnumerable<string> scopes, CancellationToken ct = default)
        => AcquireAsync(scopes, ct);
}
