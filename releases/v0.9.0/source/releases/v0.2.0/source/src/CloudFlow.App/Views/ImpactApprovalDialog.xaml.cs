using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Core.Operations;

namespace CloudFlow.App.Views;

/// <summary>
/// 影响分析审批对话框（设计文档 §25）：
/// 影响面广的操作禁止直接执行，必须先把"会影响什么"摆给用户看，由用户决定继续还是取消。
///
/// 文案全部来自 Job —— Handler 的 Impact Analysis 结论原样呈现，
/// 不在这里二次加工，避免界面说的和引擎算的不是一回事。
/// </summary>
public partial class ImpactApprovalDialog : CfDialogWindow
{
    public string OperationText { get; }

    public string ResourceId { get; }

    public string ImpactText { get; }

    public string AffectedText { get; }

    public ImpactApprovalDialog(OperationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        OperationText = string.IsNullOrWhiteSpace(job.Display) ? job.OperationType : job.Display;
        ResourceId = job.ResourceId;
        ImpactText = string.IsNullOrWhiteSpace(job.Summary)
            ? "该操作需要审批，但引擎未提供影响说明。"
            : job.Summary;
        AffectedText = job.ImpactAffectedResources is { } count and > 0
            ? $"预计影响资源数：{count}"
            : "";

        InitializeComponent();
        DataContext = this;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
