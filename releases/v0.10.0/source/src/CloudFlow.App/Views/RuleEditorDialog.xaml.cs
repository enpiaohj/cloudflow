using System.ComponentModel;
using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.App.Views;

/// <summary>在同一窗口中完成输入、引擎影响确认和执行反馈。</summary>
public partial class RuleEditorDialog : CfSidePanelWindow
{
    private readonly NsgSecurityRule _rule;
    private readonly IReadOnlyList<NsgSecurityRule> _rules;
    private readonly Func<bool, int, Task<OperationJob>> _submit;
    private readonly Func<Guid, Task<OperationJob>> _approve;
    private readonly Func<Guid, Task<OperationJob>> _reject;
    private OperationJob? _pending;
    private bool _busy;
    public OperationJob? ResultJob { get; private set; }

    public RuleEditorDialog(NsgSecurityRule rule, IReadOnlyList<NsgSecurityRule> rules, bool priority,
        Func<bool, int, Task<OperationJob>> submit, Func<Guid, Task<OperationJob>> approve,
        Func<Guid, Task<OperationJob>> reject)
    {
        _rule = rule; _rules = rules; _submit = submit; _approve = approve; _reject = reject;
        InitializeComponent();
        RuleTitle.Text = rule.Name;
        ContextText.Text = $"{(rule.Direction == NsgRuleDirection.Inbound ? "入站" : "出站")} · {Converters.CfStatusTextConverter.Map(rule.Origin.ToString())} · {rule.Protocol} · {Converters.CfStatusTextConverter.Map(rule.Action.ToString())}\n\n源：{rule.Source}\n目标：{rule.Destination}";
        FieldBox.SelectedIndex = priority ? 1 : 0;
        UpdateField();
        Closing += OnClosing;
        ValueBox.Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Field_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ValueBox is not null && _rule is not null) UpdateField();
    }

    private void UpdateField()
    {
        var priority = FieldBox.SelectedIndex == 1;
        var current = priority ? _rule.Priority : _rule.DestinationPort;
        CurrentText.Text = $"当前值：{current}";
        ValueBox.Text = current.ToString();
        HintText.Text = priority ? "100–4096，数值越小越先匹配。每次修改一个字段，提交后按审批策略执行。" : "1–65535。协议、源和目标地址保持当前值。";
        Feedback.Text = "";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var priority = FieldBox.SelectedIndex == 1;
        if (_pending is null)
        {
            if (!int.TryParse(ValueBox.Text.Trim(), out var value) ||
                (priority ? value is < 100 or > 4096 : value is < 1 or > 65535))
            { Feedback.Text = priority ? "请输入 100–4096 的优先级。" : "请输入 1–65535 的端口。"; return; }
            if (value == (priority ? _rule.Priority : _rule.DestinationPort))
            { Feedback.Text = "尚未修改，请输入新的值。"; return; }
            var nsg = NsgRuleIds.Split(_rule.RuleId)?.NsgId;
            var conflict = priority ? _rules.FirstOrDefault(r => r.Priority == value && r.Direction == _rule.Direction &&
                !string.Equals(r.RuleId, _rule.RuleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NsgRuleIds.Split(r.RuleId)?.NsgId, nsg, StringComparison.OrdinalIgnoreCase)) : null;
            if (conflict is not null) { Feedback.Text = $"优先级已被「{conflict.Name}」使用，请换一个值。"; return; }
        }
        SetBusy(true);
        try
        {
            var job = _pending is null
                ? await _submit(priority, int.Parse(ValueBox.Text.Trim()))
                : await _approve(_pending.JobId);
            ResultJob = job;
            _pending = job.Status == JobStatus.WaitingApproval ? job : null;
            if (_pending is not null)
            {
                Feedback.Text = $"变更：{CurrentText.Text} → {ValueBox.Text}\n\n{job.Summary}\n预计影响资源数：{job.ImpactAffectedResources?.ToString() ?? "未知"}";
                Inputs.IsEnabled = false;
                SubmitButton.Content = "确认并执行";
                BackButton.Visibility = Visibility.Visible;
            }
            else if (job.Status == JobStatus.Succeeded)
            { _busy = false; CompletePanel(true); }
            else
            {
                Inputs.IsEnabled = true;
                BackButton.Visibility = Visibility.Collapsed;
                SubmitButton.Content = "重新检查";
                Feedback.Text = job.Error ?? job.Summary ?? "执行未成功，请检查输入后重试。";
            }
        }
        catch (Exception ex) { Feedback.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = CloseButton.IsEnabled = BackButton.IsEnabled = HeaderCloseButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { Inputs.IsEnabled = false; Feedback.Text = "正在检查或执行，请稍候…"; }
        else if (_pending is null) Inputs.IsEnabled = true;
    }

    private async Task<bool> DiscardPendingAsync()
    {
        if (_pending is null) return true;
        SetBusy(true);
        try
        {
            var job = await _reject(_pending.JobId);
            if (job.Status != JobStatus.Canceled) { Feedback.Text = job.Error ?? "无法取消待审批任务，请重试。"; return false; }
            _pending = null;
            ResultJob = job;
            return true;
        }
        catch (Exception ex) { Feedback.Text = ex.Message; return false; }
        finally { SetBusy(false); }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!await DiscardPendingAsync()) return;
        Inputs.IsEnabled = true;
        BackButton.Visibility = Visibility.Collapsed;
        SubmitButton.Content = "提交变更";
        Feedback.Text = "已取消待审批变更，可以继续修改。";
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    { if (!_busy && await DiscardPendingAsync()) CompletePanel(false); }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (_pending is not null)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => Close_Click(this, new RoutedEventArgs())));
        }
    }
}
