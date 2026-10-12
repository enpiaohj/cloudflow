using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 虚拟机**详情**读取（ARM <c>GET .../virtualMachines/{name}</c>）。
///
/// 与 <see cref="IVmInventoryService"/> 的分工：清单走 Azure Resource Graph（快、字段少），
/// 详情走 ARM API（一次读全）。概览页要的规格能力、安全性、休眠、创建时间、可用性区域
/// 都不在 Resource Graph 的 VM 视图里，只能这样读。
///
/// 只读接口，不含任何写操作。
/// </summary>
public interface IVmDetailService
{
    /// <summary>
    /// 读取虚拟机详情。返回 null 表示**这台 VM 在 ARM 里不存在**（已删除等）；
    /// 读取失败（权限、网络、限流）抛异常，由上层如实展示 ——
    /// 不允许把失败变成 null，那会让调用方把"读失败"当成"没有这台机器"。
    /// </summary>
    Task<VmDetailInfo?> GetAsync(string vmResourceId, CancellationToken ct = default);
}
