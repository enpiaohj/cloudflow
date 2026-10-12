namespace CloudFlow.Core.Resources;

/// <summary>
/// Resource Module 统一规范（设计文档 §24）。
/// 每种 Azure Resource Module 都实现统一接口，未来扩展为"增加模块"而不是"重新做一个软件"。
/// </summary>
public interface IResourceModule
{
    /// <summary>模块标识，如 compute。</summary>
    string ModuleId { get; }

    /// <summary>模块覆盖的 Azure 资源类型，如 Microsoft.Compute/virtualMachines。</summary>
    IReadOnlyList<string> ResourceTypes { get; }

    /// <summary>模块暴露的操作类型（Operation Engine 的 OperationType），如 vm.restart。</summary>
    IReadOnlyList<string> Commands { get; }
}
