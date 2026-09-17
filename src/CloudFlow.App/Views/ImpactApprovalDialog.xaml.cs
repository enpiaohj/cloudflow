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
    private readonly Func<Action<string>, CancellationToken, Task<ApprovalSubmitOutcome>>? _submitAsync;

    /// <summary>非空时要求的确认文本（删除资源组 / 单个资源要求输入名称本身，批量删除要求输入
    /// "删除 N 个…"，对齐 Azure Portal 删除时的交互）——大小写敏感，避免"随手打错还是通过了"。</summary>
    private readonly string? _confirmText;

    public string OperationText { get; }

    /// <summary>"目标资源"标签；批量时带上个数。</summary>
    public string TargetLabel { get; }

    public string ResourceId { get; }

    public string ImpactText { get; }

    public string AffectedText { get; }

    /// <summary>取消按钮的后果说明：单个 Job 保留在待审批，批量提交的 Job 由调用方一并作废。</summary>
    public string CancelHint { get; }

    /// <summary>确认输入框的提示文案；<see cref="RequiresConfirmText"/> 为 false 时不使用。</summary>
    public string ConfirmHint => $"请输入「{_confirmText}」以确认";

    public bool RequiresConfirmText => !string.IsNullOrEmpty(_confirmText);

    /// <summary>
    /// <paramref name="submitAsync"/> 为空时行为不变：点"继续"立刻关闭，真正的 Approve+Execute
    /// 由调用方在对话框关闭之后自己跑（<see cref="JobsViewModel"/>/<see cref="VmDetailViewModel"/>
    /// 目前就是这样用的，不强制它们跟着改）。传了这个回调，对话框才会留到 Execute 真正有结果
    /// 才关——用于删除虚拟机这种"继续"之后还有真实耗时操作、且失败时应该原地改主意重试的场景。
    /// </summary>
    /// <param name="confirmText">
    /// 非空时，"继续"按钮默认禁用，只有当确认框里的文本与此完全一致（大小写敏感）才启用——
    /// 比删除虚拟机等其它调用点的门槛更严，因为资源组删除是级联删除，破坏半径更大。
    /// </param>
    public ImpactApprovalDialog(
        OperationJob job,
        Func<Action<string>, CancellationToken, Task<ApprovalSubmitOutcome>>? submitAsync = null,
        string? confirmText = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        _submitAsync = submitAsync;
        _confirmText = confirmText;

        OperationText = string.IsNullOrWhiteSpace(job.Display) ? job.OperationType : job.Display;
        TargetLabel = "目标资源";
        ResourceId = job.ResourceId;
        ImpactText = ImpactOf(job);
        AffectedText = job.ImpactAffectedResources is { } count and > 0
            ? $"预计影响资源数：{count}"
            : "";
        CancelHint = "选择「取消」不会执行任何变更，该任务会保留在任务中心的待审批状态。";

        InitializeComponent();
        DataContext = this;
        ContinueButton.IsEnabled = !RequiresConfirmText;
    }

    /// <summary>
    /// 批量确认：一次提交的多个待审批 Job 合并成一个对话框。逐个弹 N 次既繁琐，也容易让人
    /// 机械地连点"继续"而不再细看；合并后全部目标与各自的影响一次摆全，输入确认文本的门槛
    /// 也只过一次。每个 Job 仍各自走完整流水线、各自留审计记录——合并的只是确认这一步。
    /// </summary>
    /// <param name="targetLabels">
    /// 与 <paramref name="jobs"/> 一一对应的人类可读目标说明（如"rg-test（韩国中部）"），
    /// <b>不是</b>原始 Azure Resource ID——那是一长串 <c>/subscriptions/.../providers/...</c>
    /// 路径，批量场景下堆几十行完全没法读，只在单个确认（另一个构造函数）里保留原始 ID
    /// 供需要精确核对的场景使用。
    /// </param>
    public ImpactApprovalDialog(
        IReadOnlyList<OperationJob> jobs,
        IReadOnlyList<string> targetLabels,
        string operationText,
        Func<Action<string>, CancellationToken, Task<ApprovalSubmitOutcome>> submitAsync,
        string confirmText)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(targetLabels);
        ArgumentNullException.ThrowIfNull(submitAsync);
        if (jobs.Count == 0)
        {
            throw new ArgumentException("批量确认至少需要一个待审批任务。", nameof(jobs));
        }

        if (targetLabels.Count != jobs.Count)
        {
            throw new ArgumentException("targetLabels 必须与 jobs 一一对应。", nameof(targetLabels));
        }

        _submitAsync = submitAsync;
        _confirmText = confirmText;

        OperationText = operationText;
        TargetLabel = $"目标资源（{jobs.Count} 个）";
        ResourceId = string.Join(Environment.NewLine, targetLabels);
        ImpactText = string.Join(Environment.NewLine, jobs.Select(job => $"• {ImpactOf(job)}"));
        var affected = jobs.Sum(job => job.ImpactAffectedResources ?? 0);
        AffectedText = affected > 0 ? $"预计影响资源数合计：{affected}" : "";
        CancelHint = $"选择「取消」不会执行任何变更，本次提交的 {jobs.Count} 个待审批任务会一并作废。";

        InitializeComponent();
        DataContext = this;
        ContinueButton.IsEnabled = !RequiresConfirmText;
    }

    private static string ImpactOf(OperationJob job) =>
        string.IsNullOrWhiteSpace(job.Summary)
            ? "该操作需要审批，但引擎未提供影响说明。"
            : job.Summary;

    private void ConfirmTextBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ContinueButton.IsEnabled = string.Equals(ConfirmTextBox.Text, _confirmText, StringComparison.Ordinal);
    }

    /// <summary>只复制到剪贴板，不直接填框——粘贴仍是用户的主动动作，不是"一键绕过确认"。</summary>
    private void CopyConfirmText_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_confirmText))
        {
            System.Windows.Clipboard.SetText(_confirmText);
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (RequiresConfirmText &&
            !string.Equals(ConfirmTextBox.Text, _confirmText, StringComparison.Ordinal))
        {
            return;
        }

        if (_submitAsync is null)
        {
            DialogResult = true;
            return;
        }

        SetSubmitting(true, "正在执行，请稍候…");
        try
        {
            var outcome = await _submitAsync(note => SubmittingText.Text = note, CancellationToken.None);
            if (outcome.Success)
            {
                DialogResult = true;
                return;
            }

            ShowError(outcome.ErrorMessage ?? "执行失败，原因未知。");
        }
        finally
        {
            SetSubmitting(false, "");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetSubmitting(bool submitting, string text)
    {
        SubmittingBar.Visibility = submitting ? Visibility.Visible : Visibility.Collapsed;
        SubmittingPanel.Visibility = submitting ? Visibility.Visible : Visibility.Collapsed;
        SubmittingText.Text = text;
        if (submitting)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        CancelButton.IsEnabled = !submitting;
        ContinueButton.IsEnabled = !submitting;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}

/// <summary>见 <see cref="ImpactApprovalDialog"/> 的 <c>submitAsync</c> 回调返回类型。</summary>
public sealed record ApprovalSubmitOutcome(bool Success, string? ErrorMessage)
{
    public static ApprovalSubmitOutcome Ok() => new(true, null);
    public static ApprovalSubmitOutcome Failed(string message) => new(false, message);
}
