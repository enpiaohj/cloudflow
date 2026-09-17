using System.Reflection;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 应用身份信息。版本取自程序集 <see cref="AssemblyInformationalVersionAttribute"/>，
/// 其值由 <c>Directory.Build.props</c> 的 <c>Version</c> 决定。
/// 不在界面里硬编码版本号，否则界面显示的版本会与构建出的实际版本不一致。
/// </summary>
public static class AppInfo
{
    /// <summary>产品名，与仓库 README / 窗口标题一致。</summary>
    public const string ProductName = "CloudFlow";

    /// <summary>副标题，与仓库 README 一致。</summary>
    public const string ProductSubtitle = "CloudFlow for Microsoft Azure";

    /// <summary>开发者 GitHub 用户名（仓库归属者）。「关于」页与未来的反馈入口共用。</summary>
    public const string DeveloperGitHub = "enpiaohj";

    /// <summary>源码仓库地址（GitHub，Private）。</summary>
    public const string RepositoryUrl = $"https://github.com/{DeveloperGitHub}/cloudflow";

    /// <summary>许可证名称，与仓库根目录 LICENSE 一致。</summary>
    public const string LicenseName = "GPL-3.0";

    /// <summary>形如 <c>v0.1.0-dev</c>；读取不到时为空字符串。</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>形如 <c>CloudFlow v0.1.0-dev</c>。</summary>
    public static string DisplayName =>
        string.IsNullOrEmpty(Version) ? ProductName : $"{ProductName} {Version}";

    private static string ReadVersion()
    {
        var raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // .NET SDK 会把源码版本追加为 "+<commit sha>"，那部分不面向用户展示
        var plus = raw.IndexOf('+');
        return plus >= 0 ? $"v{raw[..plus]}" : $"v{raw}";
    }
}
