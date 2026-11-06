using System.Windows;
using CloudFlow.App.ViewModels;
using CloudFlow.Core.Operations;

namespace CloudFlow.App.Infrastructure;

/// <summary>审批窗口内跟踪同一个任务，事件订阅只覆盖本次执行。</summary>
public static class ApprovalJobRunner
{
    public static async Task<OperationJob> RunAsync(IOperationEngine engine, Guid jobId,
        Action<string> progress, CancellationToken ct)
    {
        var active = true;
        void Updated(object? sender, OperationJob job)
        {
            if (job.JobId != jobId) return;
            var text = JobPresentation.ResultText(job);
            if (string.IsNullOrWhiteSpace(text)) text = Converters.CfStatusTextConverter.Map(job.Status.ToString());
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) { if (active) progress(text); }
            else dispatcher.BeginInvoke(() => { if (active) progress(text); });
        }
        engine.JobUpdated += Updated;
        try { return await engine.ApproveAsync(jobId, ct); }
        finally { active = false; engine.JobUpdated -= Updated; }
    }
}
