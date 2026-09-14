namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// VM 电源状态。Power Off（Stopped）与 Deallocate 必须严格区分（设计文档 §19）。
/// </summary>
public enum VmPowerState
{
    /// <summary>运行中。</summary>
    Running,

    /// <summary>已关机（Stopped / Power Off），计算资源仍保留，计费可能继续。</summary>
    Stopped,

    /// <summary>已解除分配（Deallocated），计算资源已释放，不再收取计算费用。</summary>
    Deallocated
}

/// <summary>VM 操作系统类型。</summary>
public enum VmOsType
{
    Windows,

    Linux
}
