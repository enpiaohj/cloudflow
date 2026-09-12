namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// Demo 模式下的账号上下文常量。
/// 真实 Azure 接入后由 AccountSession（AccountId / TenantId）替换。
/// </summary>
public sealed class MockAccountContext
{
    public const string DemoAccountId = "demo-account";
    public const string DemoTenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    public string AccountId => DemoAccountId;

    public string TenantId => DemoTenantId;

    public string AccountDisplayName => "Contoso";
}
