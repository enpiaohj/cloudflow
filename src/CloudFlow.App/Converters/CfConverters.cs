using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudFlow.App.Converters;

/// <summary>
/// 状态 → 颜色（背景 / 前景）。
/// 覆盖：VM 状态、Job 状态、NSG Action / Origin（与概念图配色一致）。
///
/// **不再自己造画刷**：从 <c>CfThemeManager</c> 取 <c>Cf.Status.&lt;档位&gt;.&lt;Bg|Fg&gt;</c> 的
/// **可变实例**。原来这里每次 <c>new SolidColorBrush(...)</c>，切主题时这些徽章永远不会变 ——
/// 而它们恰恰是页面上最显眼的一类着色元素。
///
/// 刻意**不走 <c>Application.Current.TryFindResource</c>**：资源树里的 Freezable 会被 WPF 冻结，
/// 而绑定求过一次值就不会重跑 —— 换字典条目通知不到这些徽章。取的必须是
/// <c>CfThemeManager</c> 手里那批不进资源树的实例，切主题时就地改色才会重绘。
/// </summary>
public sealed class CfStatusBrushConverter : IValueConverter
{
    public CfConverterOutput Mode { get; set; } = CfConverterOutput.Background;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Brush(value?.ToString() ?? "", Mode);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>
    /// 状态键 → 调色板档位。档位名不带颜色（不叫"绿/红"），
    /// 因为同一个档位在浅色与深色两套里的实际颜色完全不同。
    /// </summary>
    private static string Tone(string key) => key switch
    {
        "Running" or "Succeeded" or "Allow" or "Protected" => "Success",
        // 未关联 NSG 是安全发现，用告警色；状态未知用中性色，不能混进"安全"里
        "Unprotected" => "Warning",
        "Warning" or "WaitingApproval" => "Warning",
        "Failed" or "Deny" => "Danger",
        // 风险三档与状态共用徽章配色：高=危险、中=警告；
        // 低用中性而不是成功色 —— "低风险"不是正面结论，绿色会被读成"好"
        "High" => "Danger",
        "Medium" => "Warning",
        "Low" => "Neutral",
        "Unknown" or "Stopped" or "Deallocated" or "Canceled" => "Neutral",
        "Subnet" => "Subnet",
        // NIC、以及 Validating / AnalyzingImpact / WaitingAzure 等中间态用信息色
        _ => "Info"
    };

    private static Brush Brush(string key, CfConverterOutput mode)
    {
        var resourceKey = $"Cf.Status.{Tone(key)}.{(mode == CfConverterOutput.Background ? "Bg" : "Fg")}";

        if (Themes.CfThemeManager.LiveBrush(resourceKey) is { } fromPalette)
        {
            return fromPalette;
        }

        // 正常运行时不可达：真出现"某个档位没有 key"，CfPaletteParityTests 会先失败。
        // 用刺眼的洋红而不是灰/透明：万一真漏了，页面上是一块看得见的异色，
        // 而不是一个"看起来挺正常、只是淡了点"的灰色，免得又被当成设计如此。
        return Brushes.Magenta;
    }
}

public enum CfConverterOutput
{
    Background,
    Foreground
}

/// <summary>可空值 → 显示文本：null 显示 "—"（如已释放 VM 的 IP / CPU）。</summary>
public sealed class CfNullableTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => "—",
            double percent => $"{percent:0}%",
            string s when s.Length == 0 => "—",
            _ => value.ToString() ?? "—"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CfBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CfInverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串非空 → Visible（操作反馈 InfoBar）。</summary>
public sealed class CfNonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 分节可见性：当前选中的分节 key（绑定源）== 本元素所属的分节 key（<c>ConverterParameter</c>）时可见。
///
/// <para>
/// 设置页改成顶部分节条后，内容区一次只渲染一个分节。用 Visibility 而不是
/// <c>ContentControl</c> + <c>DataTemplateSelector</c>：分节的 XAML 本来就在同一个文件里、
/// 直接写在各张卡片上，换模板选择器等于把所有卡片搬进资源字典再给每张配一个 DataTemplate，
/// 改动面和回退成本都不成比例。
/// </para>
/// <para>
/// 用 <c>ConverterParameter</c> 而不是给每个分节配一个 DataTrigger 样式：参数是普通字符串，
/// 一眼能看出这个 Border 属于哪一节；DataTrigger 要写 7 份一模一样的 Style，且 Value 和
/// 元素的对应关系要在两处之间来回对。
/// </para>
/// </summary>
public sealed class CfSectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>非 null → Visible（WaitingApproval 审批按钮）。</summary>
public sealed class CfNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>非 Running → Visible（"启动"按钮仅在未运行时显示）。</summary>
public sealed class CfNotRunningToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), "Running", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Azure Resource ID → 资源名（取最后一段路径）。
/// 列表里展示完整 Resource ID 会被截断成 "/subscriptions…"，用户看不出是哪个资源（概念图 1 显示 "WEB01"）。
/// </summary>
public sealed class CfResourceNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || string.IsNullOrWhiteSpace(id))
        {
            return "—";
        }

        var separator = id.TrimEnd('/').LastIndexOf('/');
        return separator >= 0 && separator < id.Length - 1 ? id[(separator + 1)..] : id;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 状态/枚举 → 中文显示文本（VM 状态、Job 状态、NSG Action/Origin、风险等级）。
/// 颜色转换器仍使用英文枚举键，本转换器只负责显示。
/// </summary>
public sealed class CfStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => "",
            Enum e => Map(e.ToString()),
            string s => Map(s),
            _ => value.ToString() ?? ""
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    public static string Map(string key) => key switch
    {
        // VM 电源状态 / 注意标记
        "Running" => "运行中",
        "Stopped" => "已停止",
        "Deallocated" => "已解除分配",
        "Warning" => "警告",
        // Job 状态机
        "Pending" => "排队中",
        "Validating" => "校验中",
        "AnalyzingImpact" => "影响分析中",
        "WaitingApproval" => "等待审批",
        "WaitingAzure" => "等待 Azure",
        "Verifying" => "验证中",
        "Succeeded" => "成功",
        "Failed" => "失败",
        "Canceled" => "已取消",
        // NSG 规则
        "Allow" => "允许",
        "Deny" => "拒绝",
        "Nic" => "网卡",
        "Subnet" => "子网",
        // 风险等级
        "Low" => "低",
        "Medium" => "中",
        "High" => "高",
        // 安全状态
        "Protected" => "受保护",
        "Unprotected" => "未关联 NSG",
        "Unknown" => "状态未知",
        _ => key
    };
}
