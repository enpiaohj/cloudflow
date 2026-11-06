namespace CloudFlow.Modules.Network.Models;

public static class NsgRulePriorities
{
    /// <summary>从默认值 400 起按 10 递增建议；整十档耗尽后才使用其他合法整数。</summary>
    public static int? Suggest(IEnumerable<int> usedPriorities)
    {
        var used = usedPriorities.ToHashSet();
        var preferred = Enumerable.Range(40, 370).Concat(Enumerable.Range(10, 30)).Select(x => x * 10);
        foreach (var priority in preferred.Concat(Enumerable.Range(400, 3697)).Concat(Enumerable.Range(100, 300)))
        {
            if (!used.Contains(priority))
            {
                return priority;
            }
        }
        return null;
    }
}
