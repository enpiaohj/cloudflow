using CloudFlow.Core.Resources;

namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// Compute 模块元数据注册（设计文档 §24 IResourceModule）。
/// </summary>
public sealed class ComputeModule : IResourceModule
{
    public const string OperationStart = "vm.start";
    public const string OperationRestart = "vm.restart";
    public const string OperationPowerOff = "vm.power_off";
    public const string OperationDeallocate = "vm.deallocate";
    public const string OperationResize = "vm.resize";
    public const string OperationSnapshot = "disk.snapshot";

    /// <summary>删除虚拟机（设计文档 v3.1 §87）。</summary>
    public const string OperationDelete = "vm.delete";

    /// <summary>创建虚拟机（设计文档 v3.1 §87）。</summary>
    public const string OperationCreate = "vm.create";

    public string ModuleId => "compute";

    public IReadOnlyList<string> ResourceTypes => ["Microsoft.Compute/virtualMachines"];

    public IReadOnlyList<string> Commands =>
    [
        OperationStart,
        OperationRestart,
        OperationPowerOff,
        OperationDeallocate,
        OperationResize,
        OperationSnapshot,
        OperationDelete,
        OperationCreate
    ];
}
