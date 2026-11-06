using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.App.Views;

/// <summary>
/// 删除虚拟机的确认框：核对目标，并选择要<b>连带删除</b>的关联资源类别。
/// </summary>
/// <remarks>
/// <para>
/// 这里按<b>类别</b>勾选（网卡 / 系统盘 / 数据盘 / 公网 IP），不列出具体资源 ——
/// 因为此刻还没读 ARM（读它需要先构造一次 OperationRequest）。
/// 具体资源名由<b>随后的影响确认框</b>（<see cref="ImpactApprovalDialog"/>）列出：
/// 那一步的影响面由 <c>DeleteVmHandler.AnalyzeImpactAsync</c> 现查真实挂载关系后生成，
/// 是权威的。两个框分工不同 —— 这个拦"点错了机器"，那个讲清"到底会删掉什么"。
/// </para>
/// <para>
/// <b>网卡 + 系统盘默认勾选</b>（对齐 Azure Portal 现在的删除向导默认行为，减少
/// "删完 VM 网卡/系统盘还在计费"这种最容易被忽略的残留）；数据盘、公网 IP 更可能是
/// 用户特意要保留的东西，继续默认不勾、要求手动确认。
/// </para>
/// <para>
/// 点"删除"不会立刻关闭——会先跑一次 <c>submitAsync</c>（Validate + Impact 分析，是一次真实
/// Azure 读取），对话框留在原地显示"正在分析影响面"，成功才关（转到 <see cref="ImpactApprovalDialog"/>
/// 走真正的 Execute），失败就地报错、留着让用户重试或改勾选。
/// </para>
/// </remarks>
public partial class DeleteVmDialog : CfDialogWindow
{
    private readonly Func<IReadOnlyCollection<VmLinkedResourceKind>, CancellationToken, Task<DeleteVmSubmitOutcome>>
        _submitAsync;

    public DeleteVmDialog(
        VmSummary vm,
        Func<IReadOnlyCollection<VmLinkedResourceKind>, CancellationToken, Task<DeleteVmSubmitOutcome>> submitAsync)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _submitAsync = submitAsync;

        InitializeComponent();

        VmNameText.Text = vm.Name;
        VmTargetText.Text = string.IsNullOrWhiteSpace(vm.IpDisplay)
            ? vm.ResourceId
            : $"{vm.IpDisplay} · {vm.ResourceId}";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var kinds = new List<VmLinkedResourceKind>();

        if (NicBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.NetworkInterface);
        }

        if (OsDiskBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.OsDisk);
        }

        if (DataDiskBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.DataDisk);
        }

        if (PublicIpBox.IsChecked is true)
        {
            kinds.Add(VmLinkedResourceKind.PublicIpAddress);
        }

        SetSubmitting(true);
        try
        {
            var outcome = await _submitAsync(kinds, CancellationToken.None);
            if (outcome.Success)
            {
                DialogResult = true;
                return;
            }

            ShowError(outcome.ErrorMessage ?? "提交失败，原因未知。");
        }
        finally
        {
            SetSubmitting(false);
        }
    }

    private void SetSubmitting(bool submitting)
    {
        SubmittingBar.Visibility = submitting ? Visibility.Visible : Visibility.Collapsed;
        SubmittingPanel.Visibility = submitting ? Visibility.Visible : Visibility.Collapsed;
        if (submitting)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        CancelButton.IsEnabled = !submitting;
        DeleteButton.IsEnabled = !submitting;
        NicBox.IsEnabled = !submitting;
        OsDiskBox.IsEnabled = !submitting;
        DataDiskBox.IsEnabled = !submitting;
        PublicIpBox.IsEnabled = !submitting;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}

/// <summary>见 <see cref="DeleteVmDialog"/> 的 <c>submitAsync</c> 回调返回类型。</summary>
public sealed record DeleteVmSubmitOutcome(bool Success, string? ErrorMessage)
{
    public static DeleteVmSubmitOutcome Ok() => new(true, null);
    public static DeleteVmSubmitOutcome Failed(string message) => new(false, message);
}
