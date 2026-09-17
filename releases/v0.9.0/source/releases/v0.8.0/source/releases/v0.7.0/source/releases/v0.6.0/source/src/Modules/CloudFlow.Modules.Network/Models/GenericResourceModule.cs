using CloudFlow.Core.Resources;

namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 单个资源清理模块元数据注册：与 <see cref="ResourceGroupModule"/> 是同一个"资源"页的两个
/// 粒度——组内某个具体资源（如遗留的虚拟网络）有时想单独删掉，不必连带整个资源组一起删。
/// </summary>
public sealed class GenericResourceModule : IResourceModule
{
    /// <summary>按 Azure Resource ID 通用删除单个资源（不区分类型）。</summary>
    public const string OperationDelete = "resource.delete";

    /// <summary>载荷键：Azure 资源类型（如 Microsoft.Network/networkInterfaces）——
    /// 只用于 Impact 描述可读性与 Validate 拦截虚拟机类型，不参与鉴权。</summary>
    public const string PayloadResourceType = "resourceType";

    public string ModuleId => "resource";

    public IReadOnlyList<string> ResourceTypes => ["*"];

    public IReadOnlyList<string> Commands => [OperationDelete];
}
