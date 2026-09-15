using CloudFlow.Core.Operations;

namespace CloudFlow.Data.Stores;

/// <summary>界面主题。System 表示跟随 Windows 的浅色 / 深色设置。</summary>
public enum AppThemeKind
{
    System,
    Light,
    Dark
}

/// <summary>
/// 应用级偏好设置。存在 <c>%LOCALAPPDATA%\CloudFlow\settings.json</c>，
/// 不含任何凭据、Token 或账户信息，所以它可以随便删 —— 删掉即回到默认值。
///
/// 每个字段的取值范围都是**离散的**（下拉框的选项），
/// <see cref="Normalize"/> 是唯一的合法性判定点：越界值一律回退默认，不抛异常。
/// 手改坏的 settings.json 不应该让应用起不来。
/// </summary>
public sealed record AppSettings
{
    /// <summary>自动刷新间隔（秒）。0 = 关闭。列表页据此起定时器。</summary>
    public static readonly IReadOnlyList<int> SupportedAutoRefreshSeconds = [0, 30, 60, 300];

    /// <summary>每页条数。</summary>
    public static readonly IReadOnlyList<int> SupportedPageSizes = [10, 25, 50, 100];

    public static readonly AppSettings Default = new();

    public AppThemeKind Theme { get; init; } = AppThemeKind.System;

    /// <summary>审批策略。默认「仅高危」，即本设置存在之前的行为。</summary>
    public ApprovalPolicy ApprovalPolicy { get; init; } = ApprovalPolicy.HighRiskOnly;

    public int AutoRefreshSeconds { get; init; }

    public int DefaultPageSize { get; init; } = 10;

    /// <summary>
    /// 是否允许向公网回显服务查询本机出口 IP（用于打开端口对话框的「我的当前 IP」）。
    /// 默认开启，因为不查就只能手填来源；但这是本应用**唯一**的对外请求，
    /// 设置页必须如实披露，不能藏在"不上报任何遥测"这句话后面。
    /// </summary>
    public bool AutoDetectPublicIp { get; init; } = true;

    /// <summary>关闭主窗口时最小化到系统托盘而不是真正退出。默认关闭——这是本设置存在
    /// 之前没有的行为，点右上角"×"却发现程序没退出、还在托盘里挂着，对没预期到这个
    /// 行为的用户是个意外，必须用户自己在设置页选择启用。</summary>
    public bool MinimizeToTrayOnClose { get; init; }

    /// <summary>
    /// 把任意来源的值收敛到合法取值。反序列化之后、使用之前都要过一遍 ——
    /// 文件可能是旧版本写的、手改过的，或者干脆是坏的。
    /// </summary>
    public AppSettings Normalize() => new()
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppThemeKind.System,
        ApprovalPolicy = Enum.IsDefined(ApprovalPolicy) ? ApprovalPolicy : ApprovalPolicy.HighRiskOnly,

        // 刷新间隔回退到 0（关闭）而不是某个正值：一个无法在下拉框里表示的间隔，
        // 下次保存时必然被改写，与其让用户看着一个"设了但没生效"的数，
        // 不如退到不做任何后台请求的那一档。
        AutoRefreshSeconds = SupportedAutoRefreshSeconds.Contains(AutoRefreshSeconds)
            ? AutoRefreshSeconds
            : 0,

        DefaultPageSize = SupportedPageSizes.Contains(DefaultPageSize) ? DefaultPageSize : 10,
        AutoDetectPublicIp = AutoDetectPublicIp,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose
    };
}
