using System.Collections.ObjectModel;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudFlow.App.ViewModels;

/// <summary>任务中心徽标弹层里的一行：Job 的展示名 + 当前子步骤说明。</summary>
public sealed record RunningJobRow(OperationJob Job)
{
    public string Display => string.IsNullOrWhiteSpace(Job.Display) ? Job.OperationType : Job.Display;

    /// <summary>没有 Handler 上报子步骤时退回粗粒度状态文案，不留空白。</summary>
    public string StatusText => string.IsNullOrWhiteSpace(Job.ProgressNote)
        ? Job.Status.ToString()
        : Job.ProgressNote;
}

/// <summary>
/// 主窗口顶栏"任务进行中"徽标（设计文档批次 5：状态机进度可见性）。
/// </summary>
/// <remarks>
/// 之前创建/删除虚拟机的进度只在触发它的那个对话框里能看到——切到别的页面、
/// 或者对话框已经关闭（比如走"批准执行"那条路径时，原来的向导早就关了），
/// 进度就彻底看不见了。这个徽标订阅全局 <see cref="IJobStore.JobChanged"/>，
/// 不管当前在哪个页面、哪个对话框开着还是关着，都能看到"现在有几个任务在跑、跑到哪一步了"。
///
/// 与 <see cref="JobsViewModel"/> 同一条订阅纪律（<see cref="ActiveAccountJobs"/> 过滤当前账户，
/// Dispatcher.BeginInvoke marshal 到 UI 线程），只是这里只关心"还没结束"的 Job。
/// </remarks>
public sealed partial class RunningJobsViewModel : ObservableObject
{
    private readonly IJobStore _jobStore;
    private readonly ScopeContext _scopeContext;

    private static readonly HashSet<JobStatus> NonTerminal =
    [
        JobStatus.Pending, JobStatus.Validating, JobStatus.AnalyzingImpact, JobStatus.WaitingApproval,
        JobStatus.Running, JobStatus.WaitingAzure, JobStatus.Verifying
    ];

    [ObservableProperty]
    private ObservableCollection<RunningJobRow> _rows = [];

    public bool HasRunning => Rows.Count > 0;

    public string Tip => Rows.Count switch
    {
        0 => "没有正在进行的任务",
        1 => $"1 个任务进行中：{Rows[0].Display}",
        _ => $"{Rows.Count} 个任务进行中"
    };

    public RunningJobsViewModel(IJobStore jobStore, ScopeContext scopeContext)
    {
        _jobStore = jobStore;
        _scopeContext = scopeContext;
        _jobStore.JobChanged += OnJobChanged;
        _scopeContext.ScopeChanged += OnScopeChanged;
    }

    private void Refresh()
    {
        Rows = [.. ActiveAccountJobs.For(_scopeContext, _jobStore.GetAll())
            .Where(job => NonTerminal.Contains(job.Status))
            .OrderBy(job => job.CreatedAt)
            .Select(job => new RunningJobRow(job))];
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(Tip));
    }

    private void OnJobChanged(object? sender, OperationJob job) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private void OnScopeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
}
