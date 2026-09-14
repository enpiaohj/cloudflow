using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 模式下的账号上下文。
/// 常量取自 <see cref="DemoIdentity"/> —— 提交端（OperationRequestFactory）与展示端必须是同一组值，
/// 否则 Demo 模式下 Job 归属会对不上。真实 Azure 接入后由 AccountSession（AccountId / TenantId）替换。
/// </summary>
public sealed class MockAccountContext
{
    public string AccountId => DemoIdentity.AccountId;

    public string TenantId => DemoIdentity.TenantId;

    public string AccountDisplayName => "Contoso";
}
