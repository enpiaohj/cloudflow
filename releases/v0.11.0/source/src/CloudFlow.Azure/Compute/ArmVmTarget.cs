using Azure.Core;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;

namespace CloudFlow.Azure.Compute;

/// <summary>
/// 把 <see cref="OperationRequest"/> 解析成虚拟机资源标识，并做类型校验。
/// </summary>
/// <remarks>
/// 抽出来是因为电源执行器与删除执行器要做同一件事。而「ID 无效」与「ID 不是虚拟机」
/// 这两条校验一旦在两个地方各写一遍，漏改一个就会让某个操作<b>绕过类型校验</b> ——
/// 对一个能删资源的执行器来说，这个后门不能留。
/// </remarks>
internal static class ArmVmTarget
{
    /// <summary>Resource ID 末段即资源名（Azure Resource ID 是唯一主键）。</summary>
    public static string NameOf(ResourceIdentifier id) => id.Name;

    /// <summary>解析并校验请求指向的必须是虚拟机。</summary>
    public static ResourceIdentifier ParseVmId(OperationRequest request)
    {
        if (!ResourceIdentifier.TryParse(request.ResourceId, out var resourceId) || resourceId is null)
        {
            throw new OperationValidationException($"无效的 Azure Resource ID：{request.ResourceId}");
        }

        if (!string.Equals(resourceId.ResourceType.ToString(), "Microsoft.Compute/virtualMachines",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"Resource ID 不是虚拟机：{resourceId.ResourceType}");
        }

        return resourceId;
    }
}
