namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 资源组内的一件资源（用于删除资源组前的 Impact 分析——级联删除影响面不像删虚拟机那样
/// 可以枚举固定几类，必须现查组里到底有什么才能如实告知用户）。
/// </summary>
/// <param name="Name">资源名称。</param>
/// <param name="Type">Azure 资源类型（如 Microsoft.Compute/virtualMachines）。</param>
/// <param name="Id">完整 Azure Resource ID——单个资源删除（<see cref="IResourceDeleteExecutor"/>）
/// 按 ID 走 ARM 通用删除接口，唯一主键，不能靠 Name+Type 反拼。</param>
/// <param name="Location">资源自身所在区域（全局资源为 global）；取不到时为空，由调用方退回资源组区域。
/// 不能直接用资源组区域代替：资源组只是逻辑容器，组里的资源可以在任何区域。</param>
public readonly record struct ResourceSummary(string Name, string Type, string Id, string Location = "")
{
    /// <summary>命中虚拟机类型时要在确认框里单独点名——防止用户把"清理网络残留"和"删掉还在用的机器"混为一谈。</summary>
    public bool IsVirtualMachine =>
        string.Equals(Type, "microsoft.compute/virtualmachines", StringComparison.OrdinalIgnoreCase);
}
