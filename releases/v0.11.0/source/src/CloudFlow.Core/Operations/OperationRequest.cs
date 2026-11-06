using CloudFlow.Core.Identity;

namespace CloudFlow.Core.Operations;

/// <summary>
/// 结构化操作请求（设计文档 §29）。
/// UI / Automation / AI 统一转成 OperationRequest 提交 Operation Engine，禁止直连 Azure SDK。
/// </summary>
public sealed class OperationRequest
{
    /// <summary>操作类型，如 vm.restart / network.change_port。</summary>
    public required string OperationType { get; init; }

    /// <summary>认证上下文三要素（设计文档 §31）：多账号场景下缺一不可。</summary>
    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>
    /// 账户的可读名称（DisplayName，缺失时回退 Username），仅用于展示与审计可读性。
    /// 随请求一起传递而非由引擎反查 ScopeContext —— 身份必须完全来自请求，
    /// 且 Job 落盘后即使账户被移除，历史记录仍应能说清是谁执行的。
    /// </summary>
    public string AccountDisplayName { get; init; } = "";

    public required string SubscriptionId { get; init; }

    /// <summary>真实 Azure 操作使用的身份 Provider；Demo 操作不需要此值。</summary>
    public AuthenticationProviderType? ProviderType { get; init; }

    /// <summary>Provider 私有 Profile 标识；仅 EmbeddedAzureCli 身份使用。</summary>
    public string? ProviderProfileId { get; init; }

    /// <summary>Azure Resource ID（资源唯一主键）。</summary>
    public required string ResourceId { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>操作附加参数（端口、目标规格等）。</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } =
        new Dictionary<string, string>();

    /// <summary>true 表示用户已在 UI 确认，跳过 WaitingApproval（审批判定仍由 Impact Analysis 决定）。</summary>
    public bool PreApproved { get; init; }

    /// <summary>展示用描述，如 "Restart VM WEB01"。</summary>
    public string Display { get; init; } = "";
}
