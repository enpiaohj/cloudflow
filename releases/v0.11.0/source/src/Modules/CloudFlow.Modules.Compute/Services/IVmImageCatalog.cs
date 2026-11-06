namespace CloudFlow.Modules.Compute.Services;

/// <summary>
/// 镜像目录：查一个市场镜像（<c>publisher:offer:sku:version</c>）实际是 Hyper-V Gen1 还是 Gen2。
///
/// 光看规格下拉标"仅支持 Gen1"不够用——用户还得知道自己选的镜像到底是哪一代，两边对不上才是
/// 真正会被 Azure 拒绝的组合（400 "cannot boot Hypervisor Generation"）。这个信息不在规格 API
/// 里，只能查镜像本身。
///
/// 与 <see cref="IRegionCatalog"/>/<see cref="IVmSizeCatalog"/> 同一类"只读目录"接口：
/// 查询失败时返回 null，调用方直接不显示这行提示，不影响创建流程本身。
/// </summary>
public interface IVmImageCatalog
{
    /// <summary>返回 "V1" 或 "V2"；镜像不存在、区域不可用或查询失败时返回 null。</summary>
    Task<string?> GetHyperVGenerationAsync(
        string subscriptionId, string region, string imageUrn, CancellationToken ct = default);
}
