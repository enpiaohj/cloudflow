namespace CloudFlow.Data.Stores;

/// <summary>
/// 本地数据目录约定：%LOCALAPPDATA%\CloudFlow\
/// </summary>
public static class CloudFlowPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudFlow");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static string AuditFile => Path.Combine(Root, "audit.jsonl");

    public static string SavedScopesFile => Path.Combine(Root, "saved-scopes.json");

    public static string JobsFile => Path.Combine(Root, "jobs.json");

    public static string ActiveAccountFile => Path.Combine(Root, "account-context.json");

    /// <summary>个人 Microsoft 账户（Embedded Azure CLI 身份）非敏感元数据；不含 Token。</summary>
    public static string PersonalAccountsFile => Path.Combine(Root, "personal-accounts.json");

    /// <summary>应用级偏好设置（主题 / 审批策略 / 列表刷新与分页 / 公网 IP 自动查询）。</summary>
    public static string AppSettingsFile => Path.Combine(Root, "settings.json");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>崩溃 / 未处理异常日志（保留最近 10 个，便于事后定位）。</summary>
    public static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var file = Path.Combine(LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{exception}");
            // 清理旧日志
            foreach (var old in Directory.GetFiles(LogsDirectory, "crash-*.log")
                         .OrderByDescending(File.GetCreationTime)
                         .Skip(10))
            {
                File.Delete(old);
            }
        }
        catch
        {
            // 日志写入失败不能再抛（避免掩盖原始异常）
        }
    }
}
