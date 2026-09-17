namespace CloudFlow.Core.Operations;

/// <summary>
/// Demo（未登录 Azure）身份的固定常量。
///
/// 与真实身份的唯一区别是：Demo 请求不带 ProviderType，执行器据此走 Mock 路径。
/// 常量放在 Core 而不是各 Mock 服务里，是为了让"演示身份"只有一处定义 ——
/// 提交端（<see cref="OperationRequestFactory"/>）与展示端（MockAccountContext / Home 演示数据）
/// 引用的必须是同一组值，否则 Demo 模式下 Job 归属会对不上。
/// </summary>
public static class DemoIdentity
{
    public const string AccountId = "demo-account";

    public const string TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
}
