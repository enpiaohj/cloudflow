namespace CloudFlow.App.Infrastructure;

/// <summary>
/// Azure 区域代码 → 中文显示名（对齐 Azure 门户中文版的叫法）。
/// </summary>
/// <remarks>
/// Resource Graph / ARM 返回的是 <c>koreacentral</c> 这类代码，演示数据里是 <c>East US</c> 这类英文名，
/// 两种写法都先归一化（去空格、转小写）再查表。不认识的区域原样返回——宁可显示代码，
/// 也不猜一个可能错误的中文名。原始代码始终留在 ToolTip 里，给习惯用 CLI 的人对照。
/// </remarks>
public static class AzureRegionCatalog
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["global"] = "全局",
        ["eastasia"] = "东亚",
        ["southeastasia"] = "东南亚",
        ["koreacentral"] = "韩国中部",
        ["koreasouth"] = "韩国南部",
        ["japaneast"] = "日本东部",
        ["japanwest"] = "日本西部",
        ["centralindia"] = "印度中部",
        ["southindia"] = "印度南部",
        ["westindia"] = "印度西部",
        ["australiaeast"] = "澳大利亚东部",
        ["australiasoutheast"] = "澳大利亚东南部",
        ["australiacentral"] = "澳大利亚中部",
        ["eastus"] = "美国东部",
        ["eastus2"] = "美国东部 2",
        ["westus"] = "美国西部",
        ["westus2"] = "美国西部 2",
        ["westus3"] = "美国西部 3",
        ["centralus"] = "美国中部",
        ["northcentralus"] = "美国中北部",
        ["southcentralus"] = "美国中南部",
        ["westcentralus"] = "美国中西部",
        ["canadacentral"] = "加拿大中部",
        ["canadaeast"] = "加拿大东部",
        ["brazilsouth"] = "巴西南部",
        ["northeurope"] = "北欧",
        ["westeurope"] = "西欧",
        ["uksouth"] = "英国南部",
        ["ukwest"] = "英国西部",
        ["francecentral"] = "法国中部",
        ["germanywestcentral"] = "德国中西部",
        ["switzerlandnorth"] = "瑞士北部",
        ["norwayeast"] = "挪威东部",
        ["swedencentral"] = "瑞典中部",
        ["polandcentral"] = "波兰中部",
        ["italynorth"] = "意大利北部",
        ["spaincentral"] = "西班牙中部",
        ["uaenorth"] = "阿联酋北部",
        ["qatarcentral"] = "卡塔尔中部",
        ["israelcentral"] = "以色列中部",
        ["southafricanorth"] = "南非北部",
        ["mexicocentral"] = "墨西哥中部",
        ["chinaeast2"] = "中国东部 2",
        ["chinaeast3"] = "中国东部 3",
        ["chinanorth2"] = "中国北部 2",
        ["chinanorth3"] = "中国北部 3"
    };

    /// <summary>区域代码或英文名 → 中文名；空值返回空串，不认识的原样返回。</summary>
    public static string DisplayName(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return "";
        }

        var key = region.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return Names.TryGetValue(key, out var name) ? name : region;
    }

    /// <summary>详情页用：中文名后附原始代码，如"韩国中部（koreacentral）"。不认识的只返回原值。</summary>
    public static string DisplayNameWithCode(string? region)
    {
        var name = DisplayName(region);
        return string.Equals(name, region, StringComparison.Ordinal) || string.IsNullOrEmpty(name)
            ? name
            : $"{name}（{region}）";
    }
}
