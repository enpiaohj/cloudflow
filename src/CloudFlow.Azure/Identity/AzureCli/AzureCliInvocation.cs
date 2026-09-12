namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>一次 Azure CLI 进程调用的参数（P0 Spike 步骤 2）。</summary>
public sealed record AzureCliInvocation
{
    /// <summary>可执行文件完整路径（如 &lt;runtime&gt;\AzureCLI\bin\az.cmd）。禁止裸命令名，杜绝 PATH 依赖。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>白名单受控的参数列表（仅允许认证/订阅发现子命令，禁止资源管理命令）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// 本进程专用的 AZURE_CONFIG_DIR（Profile 隔离）。
    /// 为 null 时强制从子进程环境中移除该变量，防止误用用户全局 Profile。
    /// </summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>超时；到期终止整个进程树。默认 5 分钟。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// 逐行输出回调（stdout / stderr 均触发）。用于设备码登录等需要实时展示 CLI 提示的场景；
    /// 输出内容可能含敏感值，进入日志前必须经 <see cref="AzureCliOutputRedactor"/> 脱敏。
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }
}
