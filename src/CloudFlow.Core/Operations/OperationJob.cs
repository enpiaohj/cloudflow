using CloudFlow.Core.Identity;

namespace CloudFlow.Core.Operations;

/// <summary>
/// Operation Job（设计文档 §30/§31）。
/// 每个 Azure Write Operation 都形成 Job，必须保存完整认证上下文，
/// 不能用 VM Name 作为唯一标识。
/// </summary>
public sealed class OperationJob
{
    public Guid JobId { get; init; } = Guid.NewGuid();

    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public AuthenticationProviderType? ProviderType { get; init; }

    public string? ProviderProfileId { get; init; }

    public required string ResourceId { get; init; }

    public required string OperationType { get; init; }

    public string Display { get; init; } = "";

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>Azure 侧 Request ID（Execute 阶段获得）。</summary>
    public string? RequestId { get; set; }

    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>结果摘要，如 "Restart accepted by Azure"。</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// 影响分析预估的受影响资源数（§25）。等待审批时由 Engine 从 ImpactAssessment 写入；
    /// 只有 Summary 是文字，界面需要数字单独呈现"会影响多少东西"。
    /// </summary>
    public int? ImpactAffectedResources { get; set; }

    public string AccountDisplayName { get; set; } = "";

    /// <summary>
    /// 停在 WaitingApproval 时那份待审批的请求。审批通过后由 Engine 清空。
    ///
    /// 存进 Job 本身，是为了让"等待审批"熬得过应用重启：挂起上下文原本只在
    /// OperationEngine 的内存字典里，而 Job 是落盘的 —— 重启后 Job 还在、
    /// 上下文没了，那个 Job 就永远卡在 WaitingApproval，既批不了也执行不了。
    /// 不新增文件、不新增 Store：随 Job 已有的那次 UpdateAsync 一起落盘。
    ///
    /// **安全约束**：OperationRequest 的字段全是 string / enum / bool / 字符串字典，
    /// 不含 Token、凭据或 CloudAccessToken，所以它可以落盘。
    /// 新增字段时必须重新核对这一点 —— 见 JsonJobStorePendingRequestTests。
    ///
    /// 旧版本写入的 WaitingApproval Job 反序列化后这里是 null，这类 Job 无法恢复审批，
    /// UI 必须明说并给"重新提交"出口，而不是让用户点了"批准"才报错。
    /// </summary>
    public OperationRequest? PendingRequest { get; set; }
}
