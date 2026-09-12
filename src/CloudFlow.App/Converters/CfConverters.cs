using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudFlow.App.Converters;

/// <summary>
/// 状态 → 颜色（背景 / 前景）。
/// 覆盖：VM 状态、Job 状态、NSG Action / Origin（与概念图配色一致）。
/// </summary>
public sealed class CfStatusBrushConverter : IValueConverter
{
    public CfConverterOutput Mode { get; set; } = CfConverterOutput.Background;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? "";
        return Mode == CfConverterOutput.Background ? Background(key) : Foreground(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static (Color bg, Color fg) Palette(string key) => key switch
    {
        "Running" or "Succeeded" or "Allow" or "Protected" => (Color.FromRgb(0xE6, 0xF4, 0xEA), Color.FromRgb(0x13, 0x73, 0x33)),
        "Stopped" or "Deallocated" or "Canceled" => (Color.FromRgb(0xEE, 0xF1, 0xF5), Color.FromRgb(0x5B, 0x64, 0x72)),
        "Warning" or "WaitingApproval" => (Color.FromRgb(0xFE, 0xF7, 0xE0), Color.FromRgb(0xB0, 0x60, 0x00)),
        "Failed" or "Deny" => (Color.FromRgb(0xFC, 0xE8, 0xE6), Color.FromRgb(0xC5, 0x22, 0x1F)),
        "Subnet" => (Color.FromRgb(0xED, 0xE7, 0xF6), Color.FromRgb(0x6A, 0x1B, 0x9A)),
        "NIC" => (Color.FromRgb(0xE8, 0xF0, 0xFE), Color.FromRgb(0x19, 0x67, 0xD2)),
        // Validating / AnalyzingImpact / WaitingAzure / Running(Job) 等中间态用蓝色
        _ => (Color.FromRgb(0xE8, 0xF0, 0xFE), Color.FromRgb(0x19, 0x67, 0xD2))
    };

    private static Brush Background(string key)
    {
        var (bg, _) = Palette(key);
        return new SolidColorBrush(bg);
    }

    private static Brush Foreground(string key)
    {
        var (_, fg) = Palette(key);
        return new SolidColorBrush(fg);
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

/// <summary>非 null → Visible（WaitingApproval 审批按钮）。</summary>
public sealed class CfNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
