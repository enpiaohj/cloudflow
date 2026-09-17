using CloudFlow.Core.Operations;

namespace CloudFlow.App.ViewModels;

/// <summary>批量删除一轮执行下来的汇总（资源组页与所有资源页共用）。</summary>
internal sealed class BatchDeleteResult
{
    public int Succeeded { get; set; }

    /// <summary>每一项失败的完整说明（含是哪一项、为什么），直接给提示条用。</summary>
    public List<string> Failures { get; } = [];

    /// <summary>
    /// 提示条文案与严重级别。部分失败时必须逐项列出——只说"有 2 项失败"，用户还得去任务中心
    /// 一条条翻才知道是哪两项、为什么。
    /// </summary>
    /// <param name="unit">量词 + 名词，如"个资源组""项资源"。</param>
    /// <param name="notSubmitted">提交阶段就没通过（如校验失败）、根本没进入确认的项。</param>
    public (string Text, string Severity) Describe(string unit, IReadOnlyList<string> notSubmitted)
    {
        var failures = notSubmitted.Concat(Failures).ToList();
        if (failures.Count == 0)
        {
            return ($"已删除 {Succeeded} {unit}，结果已验证。", nameof(JobStatus.Succeeded));
        }

        var head = Succeeded > 0
            ? $"已删除 {Succeeded} {unit}，{failures.Count} 项失败："
            : $"{failures.Count} 项删除失败：";
        return (head + string.Join("；", failures), nameof(JobStatus.Failed));
    }
}

internal static class BatchDeletion
{
    /// <summary>
    /// 作废本轮批量提交里仍停在待审批的 Job。批量删除一次会提交 N 个 Job，用户在合并确认框里
    /// 选了取消（或提交中途出错）时，不能把 N 个待审批任务留在任务中心——那既不是用户想要的，
    /// 也会让人以为这些删除还"排着队"。
    /// </summary>
    public static async Task RejectPendingAsync(IOperationEngine engine, IEnumerable<OperationJob> jobs)
    {
        foreach (var job in jobs.Where(job => job.Status == JobStatus.WaitingApproval).ToList())
        {
            try
            {
                await engine.RejectAsync(job.JobId, "用户取消了批量删除").ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 某一项作废失败不影响其它项；它仍可在任务中心手动作废。
            }
        }
    }
}
