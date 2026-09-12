using System.Collections.ObjectModel;
using CloudFlow.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// Jobs Center（设计文档 §32）：整个产品统一的操作总线视图。
/// 可按 Status 筛选；Account/Subscription/Operation 筛选在后续迭代补充。
/// </summary>
public partial class JobsViewModel : ObservableObject
{
    private readonly IJobStore _jobStore;

    [ObservableProperty]
    private ObservableCollection<OperationJob> _jobs = [];

    [ObservableProperty]
    private string _statusFilter = "All";

    public IReadOnlyList<string> StatusOptions { get; } =
    [
        "All",
        "WaitingApproval",
        "Running",
        "Succeeded",
        "Failed"
    ];

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

        if (StatusFilter != "All")
        {
            query = query.Where(j => j.Status.ToString() == StatusFilter);
        }

        Jobs = [.. query];
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyFilter);
    }
}
