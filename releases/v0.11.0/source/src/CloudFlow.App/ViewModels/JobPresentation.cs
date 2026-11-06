using CloudFlow.App.Converters;
using CloudFlow.App.Infrastructure;
using CloudFlow.Core.Operations;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 任务（Job）在界面上的文案：任务中心的"结果"列与各页面操作反馈横幅共用。
/// </summary>
/// <remarks>
/// Operation Engine 写进 Job 的 Summary / Error 是面向审计日志的机器语（如
/// <c>vm.power_off verified.</c>），本地任务历史里也已经存了一批这样的记录。
/// 翻译放在展示层而不是改引擎：审计日志保持稳定的英文标记，历史记录也能一并显示成中文。
/// 原来四个页面各写一份 <c>ShowJob</c> 的 switch，文案已经开始漂移，这里收成一份。
/// </remarks>
public static class JobPresentation
{
    /// <summary>引擎机器语 → 中文；其余文本（Handler 写的中文说明、Azure 的错误原文）原样返回。</summary>
    public static string Humanize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.EndsWith(" verified.", StringComparison.Ordinal))
        {
            return "已完成，结果已验证。";
        }

        return text switch
        {
            "Verification failed." => "操作已执行，但结果验证未通过。",
            "Operation canceled." => "操作已取消。",
            _ => text
        };
    }

    /// <summary>
    /// 任务中心"结果"列：执行中显示子步骤进度；失败显示失败原因（原来显示的是失败前最后一条
    /// 进度，比如"资源组已创建"，会被读成"结果"）；其余显示结果说明。
    /// </summary>
    public static string ResultText(OperationJob job)
    {
        // 进度只对仍在执行的任务有意义。旧版本持久化下来的失败任务里还残留着最后一条进度
        // （如"资源组已创建"），已结束的任务不能再把它当成结果显示。
        var finished = job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled;
        if (!finished && !string.IsNullOrEmpty(job.ProgressNote))
        {
            return job.ProgressNote;
        }

        if (job.Status == JobStatus.Failed && !string.IsNullOrWhiteSpace(job.Error))
        {
            return Humanize(job.Error);
        }

        return Humanize(job.Summary);
    }

    /// <summary>
    /// 批量删除确认框里一个目标的显示标签，如 <c>rg-test（koreacentral · 韩国中部）</c>。
    /// </summary>
    /// <remarks>
    /// 真实值（区域代码）必须留着——中文名是翻译，翻译可能有歧义或漏收（<see cref="AzureRegionCatalog"/>
    /// 是手工维护的对照表，不认识的区域会原样返回代码，这时候只显示代码也不能被误读成"翻译失败
    /// 就不显示了"）；中文名放在真实值后面只是方便读，不能取代真实值本身。与详情页
    /// <see cref="AzureRegionCatalog.DisplayNameWithCode"/>（中文名在前）刻意用了不同的顺序——
    /// 那里是给人"认识这是哪"，这里是批量删除确认，用户需要先核对的是"是不是这个真实区域"。
    /// </remarks>
    public static string BatchTargetLabel(string name, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return name;
        }

        var chineseName = AzureRegionCatalog.DisplayName(location);
        return string.Equals(chineseName, location, StringComparison.Ordinal)
            ? $"{name}（{location}）"
            : $"{name}（{location} · {chineseName}）";
    }

    /// <summary>页面顶部操作反馈横幅的一句话。</summary>
    public static string Feedback(OperationJob job) => job.Status switch
    {
        JobStatus.Succeeded => $"{job.Display}：已完成，结果已验证。",
        JobStatus.Failed => string.IsNullOrWhiteSpace(job.Error)
            ? $"{job.Display}：执行失败。"
            : $"{job.Display}：执行失败，{Humanize(job.Error)}",
        JobStatus.WaitingApproval => $"{job.Display}：等待审批。",
        JobStatus.Canceled => $"{job.Display}：已取消。",
        _ => $"{job.Display}：{CfStatusTextConverter.Map(job.Status.ToString())}…"
    };
}
