using Microsoft.Win32;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// "开机时启动"的实现：写 <c>HKEY_CURRENT_USER\...\Run</c> 注册表项——这是 Windows 桌面应用
/// 最常见的自启动方式，不需要管理员权限，也不需要装一个计划任务。
///
/// 只读写 HKCU（当前用户），不碰 HKLM：自启动是"这个用户想不想要"的个人偏好，
/// 不该需要管理员权限才能勾/取消勾这一个设置页开关，也不该影响同一台机器上的其他用户。
/// </summary>
public sealed class WindowsStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppInfo.ProductName;

    /// <summary>
    /// 当前是否已注册开机启动。以注册表里的值为准，不信任 <c>AppSettings.LaunchAtStartup</c>
    /// 这个记忆值——两者本该一致，但注册表可能被用户自己手动改过（比如用系统自带的
    /// "启动"设置页/任务管理器"启动"标签页把它关掉），设置页读的应该是真实生效状态。
    /// </summary>
    public bool IsRegistered()
    {
        using var key = OpenRunKey(writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// 注册或取消开机启动。注册的目标路径是**当前正在运行的这个可执行文件**——
    /// 开发环境下（<c>dotnet run</c>）这是宿主进程路径，不是真正想要的 CloudFlow.App.exe，
    /// 但这个功能本来就只对发布出去的单文件 EXE 有意义，开发环境下打开这个开关看到的
    /// 效果不代表最终产物，不在此处特殊处理（用户不会在开发环境里用这个设置项）。
    /// </summary>
    public void SetRegistered(bool enabled)
    {
        if (!enabled)
        {
            using var key = OpenRunKey(writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        using var writableKey = OpenRunKey(writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        writableKey.SetValue(ValueName, $"\"{exePath}\"");
    }

    private static RegistryKey? OpenRunKey(bool writable) =>
        Registry.CurrentUser.OpenSubKey(RunKeyPath, writable);
}
