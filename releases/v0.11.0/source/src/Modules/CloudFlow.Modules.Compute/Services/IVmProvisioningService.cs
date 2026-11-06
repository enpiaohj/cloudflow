using CloudFlow.Core.Operations;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 创建虚拟机的提交入口（设计文档 v3.1 §87）：
/// 把向导产出转成结构化 OperationRequest 提交 Operation Engine。
/// 与 <see cref="IVmPowerService"/> 同一思路 —— 与 Provider 无关，
/// 走 Mock 还是真实 ARM 由请求携带的 ProviderType 决定。
/// </summary>
public interface IVmProvisioningService
{
    /// <summary>
    /// 提交创建请求。
    /// </summary>
    /// <param name="parameters">
    /// 载荷键见 <see cref="CreateVmHandler"/> 的 Payload* 常量。
    /// <b>密码本体绝不放进这里</b>：密码方式只带 <c>credentialId</c>，
    /// 由向导先把密码写入凭据库再提交（明文落盘 = 红线）。
    /// </param>
    Task<OperationJob> CreateAsync(
        string subscriptionId, string resourceGroupName, string region,
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);
}
