namespace CloudFlow.Core.Resources;

/// <summary>
/// 统一资源引用（设计文档 §30）。
/// Azure Resource ID 是资源唯一主键，跨模块 Relation 都基于它建立。
/// </summary>
public sealed record ResourceReference
{
    /// <summary>完整 Azure Resource ID，如 /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/virtualMachines/VM01。</summary>
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    /// <summary>资源类型，如 Microsoft.Compute/virtualMachines。</summary>
    public required string ResourceType { get; init; }

    public string SubscriptionId { get; init; } = "";

    public string ResourceGroupName { get; init; } = "";

    public string Location { get; init; } = "";
}
