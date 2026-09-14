using System.Windows;
using System.Windows.Media;
using CloudFlow.Data.Stores;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CloudFlow.App.Themes;

/// <summary>
/// CloudFlow 主题切换。颜色只有一份来源（<c>Themes/CfPalette.*.xaml</c>），
/// 但要按**两条消费路径各自的机制**分别投送：
///
/// <list type="number">
/// <item><b>样式里的 <c>{DynamicResource Cf.X}</c></b> —— 切主题时**换掉资源字典里的条目**，
/// DynamicResource 会重新解析并拿到新画刷。</item>
/// <item><b>转换器直接返回的实例</b>（<see cref="Converters.CfStatusBrushConverter"/> 绑在 DataGrid 单元格上）
/// —— 绑定求值过一次就不会再跑，换条目对它无效，只能是**就地改那个实例的 <c>.Color</c>**。</item>
/// </list>
///
/// 于是每个 key 有两份画刷：一份在 <see cref="Live"/> 里（进出 <see cref="ApplyPalette"/> 就地改色，
/// 供路径 2），一份在 <c>Application.Resources</c> 顶层（每次切主题整体替换，供路径 1）。
/// 两份的取值同源，不会出现颜色不一致。
///
/// **为什么不合并成一份**：<c>Application.Resources</c> 会把它收到的每一个 Freezable **当场冻结** ——
/// 实测（本 WPF 版本）：直接插顶层 frozen=True；插进自建字典再 merge frozen=True；
/// 甚至把自建字典整体赋给 <c>Application.Resources</c> 也立刻 frozen=True；
/// 而同一个画刷插进一个自建的、不挂到 Application 上的字典则 frozen=False。
/// 冻结的 <c>SolidColorBrush</c> 赋 <c>.Color</c> 抛 <c>InvalidOperationException</c>，
/// XAML 里的 <c>po:Freeze="False"</c> 也**实测无效**。
/// 所以「可变的实例」只能活在资源树之外，就是 <see cref="Live"/>。
///
/// 替换已有条目是允许的（实测 <c>Application.Resources["k"] = newBrush</c> 不抛），
/// 这正是路径 1 成立的前提。
/// </summary>
public static class CfThemeManager
{
    private static readonly Uri LightPaletteUri =
        new("pack://application:,,,/CloudFlow.App;component/Themes/CfPalette.Light.xaml", UriKind.Absolute);

    private static readonly Uri DarkPaletteUri =
        new("pack://application:,,,/CloudFlow.App;component/Themes/CfPalette.Dark.xaml", UriKind.Absolute);

    /// <summary>
    /// 可变的画刷实例，**绝不放进任何 ResourceDictionary**（放进去就会被冻结）。
    /// 转换器从这里取实例，切主题时就地改色，已经绑定到界面上的画刷会自动重绘。
    /// </summary>
    private static readonly Dictionary<string, SolidColorBrush> Live = new(StringComparer.Ordinal);

    private static ResourceDictionary? _lightTable;
    private static ResourceDictionary? _darkTable;

    /// <summary>用户选择的档位（可能是"跟随系统"）。<see cref="Apply"/> 之后才可信。</summary>
    public static AppThemeKind Requested { get; private set; } = AppThemeKind.System;

    /// <summary>
    /// 把"跟随系统"解析掉之后的**实际**档位，只会是浅色或深色。
    /// 想知道"现在到底是不是深色"就读它 —— <see cref="Requested"/> 在跟随系统时是
    /// <see cref="AppThemeKind.System"/>，拿它判深浅会永远判成非深色。
    /// </summary>
    public static AppThemeKind Effective { get; private set; } = AppThemeKind.Light;

    /// <summary>
    /// 实际档位发生变化时触发。<see cref="AppThemeKind"/> 是解析后的浅色/深色，
    /// 不是用户选的档位 —— 订阅者关心的是"画面该不该变"，不是"用户点了哪一项"。
    /// <para>
    /// 给**不经过资源字典**的消费方用：调色板只改得了画刷，改不了 DWM 画的窗口标题栏
    /// （见 <see cref="CfDialogWindow"/>）。
    /// </para>
    /// </summary>
    public static event Action<AppThemeKind>? EffectiveChanged;

    /// <summary>已挂上系统主题监听的窗口。只跟随系统时才挂。</summary>
    private static Window? _watched;

    static CfThemeManager()
    {
        // WPF-UI 自己的主题变化（由 SystemThemeWatcher 或系统消息触发）要转成调色板变化。
        // 只有"跟随系统"档位才转：用户明确选了浅色/深色时，系统换主题不该动 CloudFlow。
        //
        // 刻意**不读事件参数**（3.0.5 里它给的不是新主题），而是重新问一次系统主题：
        // 系统主题由 Windows 决定，事件触发时必然已是新值，不依赖 WPF-UI 内部的更新顺序。
        ApplicationThemeManager.Changed += (_, _) =>
        {
            if (Requested == AppThemeKind.System)
            {
                ApplyPalette(ResolveEffective(AppThemeKind.System));
            }
        };
    }

    /// <summary>
    /// 建立 <see cref="Live"/> 里的可变画刷，并把 <c>Application.Resources</c> 顶层的条目填上
    /// （顶层条目在查找顺序上优先于 MergedDictionaries）。
    /// **必须在创建任何界面元素之前调用且只调用一次**（<c>App.OnStartup</c> 的第一步）。
    /// 初始值取浅色表 —— <see cref="Apply"/> 会立刻按用户设置改成实际档位。
    /// </summary>
    public static void Initialize()
    {
        var app = Application.Current
            ?? throw new InvalidOperationException(
                "CfThemeManager.Initialize 必须在 Application 实例存在之后调用。");

        foreach (var (name, source) in Palette(AppThemeKind.Light))
        {
            app.Resources[name] = new SolidColorBrush(source.Color);
            Live[name] = new SolidColorBrush(source.Color);
        }
    }

    /// <summary>
    /// 取 <see cref="Live"/> 里的可变画刷。给"转换器直接返回实例"那条路径用 ——
    /// 不要改用 <c>Application.Current.TryFindResource</c>：那里拿到的是被冻结的副本，
    /// 切主题时换条目不会通知已经求过值的绑定。
    /// 画刷不存在时返回 <c>null</c>，由调用方决定怎么显眼地报错。
    /// </summary>
    public static SolidColorBrush? LiveBrush(string key) =>
        Live.TryGetValue(key, out var brush) ? brush : null;

    /// <summary>
    /// 应用主题。**必须在 MainWindow.Show() 之前调用一次** —— 否则窗口会先以浅色绘制一帧，
    /// 用户看到一次白闪再变深。
    /// </summary>
    public static void Apply(AppThemeKind kind)
    {
        Requested = kind;

        var effective = ResolveEffective(kind);
        ApplyPalette(effective);

        // 让 WPF-UI 自带控件（TitleBar / DataGrid chrome / SymbolIcon / ToggleSwitch）一起跟随。
        // Backdrop 传 None：MainWindow 声明的是 WindowBackdropType="None"，
        // 传别的值会让 FluentWindow 突然去挂 Mica/Acrylic，与浅色时的观感不一致。
        ApplicationThemeManager.Apply(
            effective == AppThemeKind.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.None,
            updateAccent: true);
    }

    /// <summary>
    /// 按当前档位决定是否监听系统主题变化。**必须在窗口 Show 之后调用** ——
    /// SystemThemeWatcher 要在窗口句柄上挂消息钩子，句柄还没生成时挂不上。
    /// 重复调用是安全的：每次都先 UnWatch 再决定要不要 Watch。
    /// </summary>
    public static void WatchSystemTheme(Window window)
    {
        if (_watched is not null)
        {
            SystemThemeWatcher.UnWatch(_watched);
            _watched = null;
        }

        if (Requested != AppThemeKind.System)
        {
            return;
        }

        SystemThemeWatcher.Watch(window, WindowBackdropType.None, updateAccents: true);
        _watched = window;
    }

    /// <summary>把"跟随系统"解析成确定的一档。系统主题读不出来时按浅色处理。</summary>
    private static AppThemeKind ResolveEffective(AppThemeKind kind) => kind switch
    {
        AppThemeKind.Light => AppThemeKind.Light,
        AppThemeKind.Dark => AppThemeKind.Dark,
        _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
            ? AppThemeKind.Dark
            : AppThemeKind.Light
    };

    private static void ApplyPalette(AppThemeKind effective)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        // 只在真的换档时才通知。跟随系统时这个方法是会被反复调用的
        // （每次系统主题消息都走一遍），无脑广播等于让每个对话框跟着做无谓的 DWM 调用。
        // 注意判据是**解析后**的档位：用户从"跟随系统"改成"深色"而系统本来就是深色时，
        // Requested 变了但画面不该动，也不该通知。
        var changed = Effective != effective;
        Effective = effective;

        var missing = new List<string>();
        foreach (var (name, source) in Palette(effective))
        {
            if (!Live.TryGetValue(name, out var live))
            {
                // 走到这里说明 Initialize() 没被调用，或者有人加了 key 而没加进调色板文件。
                // 不吞掉：静默跳过的表现是"某些控件在深色下仍是浅色"，比直接报出来难查得多。
                missing.Add(name);
                continue;
            }

            // 路径 2：就地改色。同一个实例，已绑定的单元格跟着重绘。
            live.Color = source.Color;

            // 路径 1：换条目。DynamicResource 重新解析，样式跟着重绘。
            // 新画刷插进去会被冻结，无所谓 —— 下一轮切主题是再换一个，而不是改它。
            app.Resources[name] = new SolidColorBrush(source.Color);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"有 {missing.Count} 个主题画刷没有装进 CfThemeManager（首个：{missing[0]}）。" +
                "请确认 App.OnStartup 在应用主题之前调用了 CfThemeManager.Initialize()，" +
                "且该 key 已在 Themes/CfPalette.Light.xaml 中定义。");
        }

        // 放在最后：订阅者收到通知时会去读 Effective，先更新再广播，
        // 别让它们读到上一轮的旧值。抛异常时也不广播 —— 那一轮画面本来就没换成功。
        if (changed)
        {
            EffectiveChanged?.Invoke(effective);
        }
    }

    /// <summary>遍历一套调色板里所有"值是 <see cref="SolidColorBrush"/> 的字符串 key"。</summary>
    private static IEnumerable<(string Name, SolidColorBrush Source)> Palette(AppThemeKind effective)
    {
        var table = Table(effective);
        foreach (var key in table.Keys)
        {
            if (key is string name && table[key] is SolidColorBrush source)
            {
                yield return (name, source);
            }
        }
    }

    private static ResourceDictionary Table(AppThemeKind effective) => effective switch
    {
        AppThemeKind.Dark => _darkTable ??= Load(DarkPaletteUri),
        _ => _lightTable ??= Load(LightPaletteUri)
    };

    private static ResourceDictionary Load(Uri uri) => new() { Source = uri };
}
