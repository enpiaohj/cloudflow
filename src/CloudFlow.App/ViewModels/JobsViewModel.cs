using System.Collections.ObjectModel;
using CloudFlow.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 任务中心（设计文档 §32）：整个产品统一的操作总线视图。
/// 可按状态筛选；Account/Subscription/Operation 筛选在后续迭代补充。
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

    [ObservableProperty]
    private ObservableCollection<OperationJob> _jobs = [];

    [ObservableProperty]
    private string _statusFilter = "全部";

    public IReadOnlyList<string> StatusOptions { get; } =
        ["全部", .. StatusFilterMap.Keys];

    public JobsViewModel(IJobStore jobStore)
    {
        _jobStore = jobStore;
        _jobStore.JobChanged += OnJobChanged;
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        ApplyFilter();
        return Task.CompletedTask;
    }

    partial void OnStatusFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<OperationJob> query = _jobStore.GetAll();

        if (StatusFilter != "全部" && StatusFilterMap.TryGetValue(StatusFilter, out var key))
        {
            query = query.Where(j => j.Status.ToString() == key);
        }

        Jobs = [.. query];
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyFilter);
    }
}
