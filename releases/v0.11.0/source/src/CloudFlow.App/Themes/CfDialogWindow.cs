using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CloudFlow.Data.Stores;

namespace CloudFlow.App.Themes;

/// <summary>
/// 对话框基类：把**系统画的标题栏**按当前主题切成深色。
///
/// <para>
/// 为什么需要它：四个对话框都是光秃秃的 <see cref="Window"/>，标题栏由 DWM 绘制，
/// 而 DWM 只认**系统**的浅色/深色设置，不认 CloudFlow 自己的主题档位。
/// 系统是浅色、用户把 CloudFlow 设成深色时，对话框正文已经是深色底浅色字，
/// 顶上却横着一条**白底黑字**的标题栏 —— 这正是"深色模式下还有黑色字体"的来源。
/// </para>
///
/// <para>
/// 为什么不改成 <c>ui:FluentWindow</c>：FluentWindow 的标题栏画在**客户区**里
/// （<c>ExtendsContentIntoTitleBar</c>），换过去得给每个对话框补一个 <c>ui:TitleBar</c>
/// 并重排现有布局。而这里要修的只是标题栏的配色，DWM 的一个属性就够，
/// 且保留原生标题栏 —— 拖动、双击最大化、系统菜单都不必自己实现。
/// </para>
///
/// <para>
/// 用基类而不是附加行为：四个对话框的 code-behind 本来就是 <c>partial class X : Window</c>，
/// 换基类是一行改动，且**编译期**就保证不会漏掉某个对话框。
/// </para>
/// </summary>
public class CfDialogWindow : Window
{
    /// <summary>
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>。Windows 10 20H1 及以后是 20，
    /// 更早的预览版用的是 19 —— 20 不被识别时回退到 19 再试一次。
    /// </summary>
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 句柄要到这里才存在；在 OnInitialized / 构造函数里调都没有窗口可设。
        ApplyTitleBarTheme();

        // 跟随系统的档位下，对话框开着的时候系统换主题，标题栏也要跟着换。
        // 主窗口那条路径由 SystemThemeWatcher 负责，它只挂在主窗口上，覆盖不到这里。
        CfThemeManager.EffectiveChanged += OnEffectiveThemeChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 静态事件持有实例会阻止窗口被回收，必须成对注销。
        CfThemeManager.EffectiveChanged -= OnEffectiveThemeChanged;
        base.OnClosed(e);
    }

    private void OnEffectiveThemeChanged(AppThemeKind kind) => ApplyTitleBarTheme();

    private void ApplyTitleBarTheme()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        var useDark = CfThemeManager.Effective == AppThemeKind.Dark ? 1 : 0;
        var hr = DwmSetWindowAttribute(
            source.Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));

        if (hr != 0)
        {
            DwmSetWindowAttribute(
                source.Handle, DwmwaUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
