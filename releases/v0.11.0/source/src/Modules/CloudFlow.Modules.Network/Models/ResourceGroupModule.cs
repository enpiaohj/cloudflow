using CloudFlow.Core.Resources;

namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 资源组清理模块元数据注册（设计文档 §24 IResourceModule）。
///
/// 放在 Network 模块而不是 Compute：资源组本身不是计算资源，这个能力存在的目的是清理
/// "创建虚拟机"流程按需新建、但删除虚拟机时刻意不连带删除的网络类残留（虚拟网络/子网）——
/// 详见设计文档 v3.2 §87.2 与 v3.1 §87.3 的边界说明。
/// </summary>
public sealed class ResourceGroupModule : IResourceModule
{
    /// <summary>删除资源组（连带删除组内全部资源，这是 Azure 的级联语义，不是本仓库自己的选择）。</summary>
    public const string OperationDelete = "resourcegroup.delete";

    public string ModuleId => "resourcegroup";

    public IReadOnlyList<string> ResourceTypes => ["Microsoft.Resources/resourceGroups"];

    public IReadOnlyList<string> Commands => [OperationDelete];
}
