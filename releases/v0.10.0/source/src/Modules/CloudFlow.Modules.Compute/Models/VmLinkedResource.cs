namespace CloudFlow.Modules.Compute.Models;

/// <summary>可与 VM 一并删除的关联资源类别（设计文档 v3.1 §87）。</summary>
public enum VmLinkedResourceKind
{
    NetworkInterface,
    OsDisk,
    DataDisk,
    PublicIpAddress
}

/// <summary>
/// VM 上一件<b>当前真实附着</b>的、可被连带删除的资源。
/// </summary>
/// <remarks>
/// <b>本类型只用于表达"读回来的真实挂载关系"，绝不用来表达"用户想删什么"。</b>
/// 后者是请求载荷，会随待审批请求落盘（<c>jobs.json</c>），因此不可信 ——
/// 若照载荷里的 ID 去删，改一个持久化文件里的字符串就能删掉任意资源。
/// 两者在 <c>DeleteVmHandler.ExecuteAsync</c> 里求交集，只删交集。
/// </remarks>
public sealed record VmLinkedResource(string ResourceId, VmLinkedResourceKind Kind, string DisplayName)
{
    /// <summary>面向用户的类别名（确认框与影响分析文案直接用）。</summary>
    public string KindText => Kind switch
    {
        VmLinkedResourceKind.NetworkInterface => "网卡",
        VmLinkedResourceKind.OsDisk => "OS 磁盘",
        VmLinkedResourceKind.DataDisk => "数据磁盘",
        _ => "公网 IP"
    };
}
