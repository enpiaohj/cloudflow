using CloudFlow.App.ViewModels;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Terminal.Security;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 按请求携带的 ProviderType 分流创建执行器：null = Demo（内存数据面），非 null = 真实 ARM。
/// 判据与 <see cref="VmPowerExecutorRouter"/> 相同：<b>不读 ScopeContext</b>，身份只来自请求。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类存在的关键理由是密码解析委托</b>（设计文档 v3.1 §87.4 硬约束②）：
/// 密码方式创建时，载荷只带凭据 Id，明文必须由这一层解出 ——
/// Modules 与 Azure 层都不该认识凭据库（那会让 Terminal 成为它们的依赖，层次倒挂），
/// 而 App 层恰好同时看得到两者，由它把"凭 Id 解密"以委托形式注入执行器。
/// </para>
/// <para>
/// 解出的明文<b>只存在于闭包与执行器调用栈里</b>：不写日志、不进异常消息、
/// 请求完成后即被 GC —— 与凭据连接流程（SshConnectFlow）同一纪律。
/// </para>
/// </remarks>
public sealed class VmProvisioningExecutorRouter(
    MockVmProvisioningExecutor mock,
    CloudFlow.Azure.Compute.ArmVmProvisioningExecutor arm,
    SshConnectionService sshConnections) : IVmProvisioningExecutor
{
    private IVmProvisioningExecutor For(OperationRequest request) =>
        request.ProviderType is null ? mock : arm;

    public Task<string?> CreateAsync(
        OperationRequest request,
        Func<CancellationToken, Task<string?>>? resolvePassword,
        CancellationToken ct = default)
    {
        // Handler 传来的委托恒为 null（它不认识凭据库）；真实解析能力由本类补上。
        // 只有载荷确实以密码方式创建时才构造闭包 —— SSH 方式不解密任何东西。
        resolvePassword ??= BuildPasswordResolver(request);

        return For(request).CreateAsync(request, resolvePassword, ct);
    }

    public Task<bool> VmReadyAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).VmReadyAsync(request, ct);

    public Task<bool> ResourceGroupExistsAsync(OperationRequest request, CancellationToken ct = default) =>
        For(request).ResourceGroupExistsAsync(request, ct);

    /// <summary>
    /// 把载荷里的凭据 Id 变成"解出管理员密码"的委托。
    /// 密文解不出（凭据被删 / 保险库来自他机）时返回 null，由执行器转为显式失败 ——
    /// 绝不静默改用别的方式创建。
    /// </summary>
    private Func<CancellationToken, Task<string?>> BuildPasswordResolver(OperationRequest request)
    {
        return async ct =>
        {
            if (!request.Payload.TryGetValue(CreateVmHandler.PayloadCredentialId, out var raw) ||
                !Guid.TryParse(raw, out var credentialId))
            {
                return null;
            }

            using var resolved = await sshConnections.CredentialLibrary
                .ResolveAsync(credentialId, ct).ConfigureAwait(false);
            return resolved?.Password;
        };
    }
}
