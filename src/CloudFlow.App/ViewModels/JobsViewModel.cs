using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 任务列表的一行：Job 本身，加上"这条现在批不批得了"的结论。
///
/// 为什么要包一层：DataGrid 的单元格只能绑属性，绑不了方法，而这个结论是按行走的。
/// 尤其是旧版本留下的 WaitingApproval Job —— 它们没有持久化待审批请求，
/// 重启后恢复不了，必须在行上直说"无法恢复审批"，
/// 而不是给一个点下去才报错的按钮。
/// </summary>
public sealed record JobRow(OperationJob Job, bool CanApprove)
{
    public bool IsWaitingApproval => Job.Status == JobStatus.WaitingApproval;

    public string ApproveText => CanApprove ? "批准" : "无法恢复审批";

    public string ApproveHint => CanApprove
        ? "查看影响面并批准执行"
        : "该任务提交于旧版本，没有保存待审批请求，重启后无法恢复审批。请在对应资源页面重新提交该操作。";
}

/// <summary>
/// 任务中心（设计文档 §32）：整个产品统一的操作总线视图。
/// 只显示当前账户的操作记录；可按状态筛选，Account/Subscription/Operation 筛选在后续迭代补充。
/// </summary>
public partial class JobsViewModel : ObservableObject
{
    /// <summary>中文筛选选项 → JobStatus 键。</summary>
    private static readonly Dictionary<string, string> StatusFilterMap = new()
    {
        ["等待审批"] = "WaitingApproval",
        ["执行中"] = "Running",
        ["成功"] = "Succeeded",
        ["失败"] = "Failed"
    };

    private readonly IJobStore _jobStore;
    private readonly IOperationEngine _engine;
    private readonly ScopeContext _scopeContext;

    [ObservableProperty]
    private ObservableCollection<JobRow> _jobs = [];

    [ObservableProperty]
    private string _statusFilter = "全部";

    public IReadOnlyList<string> StatusOptions { get; } =
        ["全部", .. StatusFilterMap.Keys];

    /// <summary>空列表要给出说明，否则会被误读为加载失败。</summary>
    public bool HasJobs => Jobs.Count > 0;

    public string EmptyText => _scopeContext.ActiveAccount is null
        ? "暂无操作记录。"
        : $"当前账户（{_scopeContext.ActiveAccount.Username}）暂无操作记录。执行启动 / 重启 / 快照 / 端口变更后会显示在这里。";

    public JobsViewModel(IJobStore jobStore, IOperationEngine engine, ScopeContext scopeContext)
    {
        _jobStore = jobStore;
        _engine = engine;
        _scopeContext = scopeContext;
        _jobStore.JobChanged += OnJobChanged;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        ApplyFilter();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在任务中心审批。这条路径存在的意义是**跨重启**：重启后详情页的内联审批已经没有上下文了，
    /// 任务中心是唯一还能把那个 Job 推下去的地方。
    /// </summary>
    [RelayCommand]
    private async Task ApproveAsync(JobRow? row)
    {
        if (row is null || !row.CanApprove)
        {
            return;
        }

        // §25：先把影响面摆给用户，由用户决定继续还是取消 —— 与详情页的内联审批同一套对话框
        var dialog = new Views.ImpactApprovalDialog(row.Job)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        await _engine.ApproveAsync(row.Job.JobId);
        ApplyFilter();
    }

    /// <summary>
    /// 作废挂起审批。批准的对偶面：不想要的挂起 Job 必须有出口，
    /// 否则它既执行不了也消失不了，只能永远挂在列表里。
    /// 「无法恢复审批」的旧 Job 同样可作废 —— 批不了，但能关掉。
    /// </summary>
    [RelayCommand]
    private async Task RejectAsync(JobRow? row)
    {
        if (row is null || !row.IsWaitingApproval)
        {
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            $"将作废任务「{row.Job.Display}」。\n\n作废后该任务不再执行，状态变为已取消，并写入审计日志。此操作不可撤销。",
            "作废任务",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

        if (!confirmed)
        {
            return;
        }

        await _engine.RejectAsync(row.Job.JobId, "任务中心作废");
        ApplyFilter();
    }

    partial void OnStatusFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<OperationJob> query = ActiveAccountJobs.For(_scopeContext, _jobStore.GetAll());

        if (StatusFilter != "全部" && StatusFilterMap.TryGetValue(StatusFilter, out var key))
        {
            query = query.Where(j => j.Status.ToString() == key);
        }

        Jobs = [.. query.Select(job => new JobRow(job, _engine.CanApprove(job.JobId)))];
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyFilter);
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyFilter);
    }
}
