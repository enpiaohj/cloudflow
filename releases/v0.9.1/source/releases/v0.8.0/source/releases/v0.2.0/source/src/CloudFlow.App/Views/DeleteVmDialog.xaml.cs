using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.App.Views;

/// <summary>
/// 删除虚拟机的确认框：核对目标，并选择要<b>连带删除</b>的关联资源类别。
/// </summary>
/// <remarks>
/// <para>
/// 这里按<b>类别</b>勾选（网卡 / 磁盘 / 公网 IP），不列出具体资源 ——
/// 因为此刻还没读 ARM（读它需要先构造一次 OperationRequest）。
/// 具体资源名由<b>随后的影响确认框</b>（<see cref="ImpactApprovalDialog"/>）列出：
/// 那一步的影响面由 <c>DeleteVmHandler.AnalyzeImpactAsync</c> 现查真实挂载关系后生成，
/// 是权威的。两个框分工不同 —— 这个拦"点错了机器"，那个讲清"到底会删掉什么"。
/// </para>
/// <para>
/// <b>默认全部不勾</b>：Azure 不连带删除这些资源，默认保持这一行为最安全；
/// 想清理的用户主动勾选。
/// </para>
/// </remarks>
public partial class DeleteVmDialog : CfDialogWindow
{
    public DeleteVmDialog(VmSummary vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        InitializeComponent();

        VmNameText.Text = vm.Name;
        VmTargetText.Text = string.IsNullOrWhiteSpace(vm.IpDisplay)
            ? vm.ResourceId
            : $"{vm.IpDisplay} · {vm.ResourceId}";
    }

    /// <summary>用户勾选要连带删除的类别；取消时为 <c>null</c>。</summary>
    public IReadOnlyCollection<VmLinkedResourceKind>? Result { get; private set; }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var kinds = new List<VmLinkedResourceKind>();

        if (NicBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.NetworkInterface);
        }

        if (DiskBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.OsDisk);
            kinds.Add(VmLinkedResourceKind.DataDisk);
        }

        if (PublicIpBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.PublicIpAddress);
        }

        Result = kinds;
        DialogResult = true;
    }
}
