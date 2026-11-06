using System.ComponentModel;
using System.Windows;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Network.Models;

namespace CloudFlow.App.Views;

public partial class ChangePriorityDialog : CfDialogWindow, INotifyPropertyChanged
{
    public string RuleName { get; }
    public int CurrentPriority { get; }
    public int NewPriority { get; private set; }
    public string PriorityText { get; set; }
    public string ErrorText { get; private set; } = "";
    private readonly NsgSecurityRule _rule;
    private readonly IReadOnlyList<NsgSecurityRule> _rules;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ChangePriorityDialog(NsgSecurityRule rule, IReadOnlyList<NsgSecurityRule> rules)
    {
        _rule = rule;
        _rules = rules;
        RuleName = rule.Name;
        CurrentPriority = rule.Priority;
        PriorityText = rule.Priority.ToString();
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => { PriorityBox.Focus(); PriorityBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PriorityText.Trim(), out var priority) || priority is < 100 or > 4096)
            ErrorText = "优先级必须是 100–4096 之间的整数。";
        else if (priority == CurrentPriority)
            ErrorText = "新优先级与当前优先级相同。";
        else
        {
            var nsgId = NsgRuleIds.Split(_rule.RuleId)?.NsgId;
            var conflict = _rules.FirstOrDefault(r => r.Direction == _rule.Direction && r.Priority == priority &&
                !string.Equals(r.RuleId, _rule.RuleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NsgRuleIds.Split(r.RuleId)?.NsgId, nsgId, StringComparison.OrdinalIgnoreCase));
            ErrorText = conflict is null ? "" : $"优先级 {priority} 已被规则「{conflict.Name}」使用。";
            if (conflict is null) { NewPriority = priority; DialogResult = true; return; }
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
    }
}
