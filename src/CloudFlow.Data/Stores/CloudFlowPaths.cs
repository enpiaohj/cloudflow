namespace CloudFlow.Data.Stores;

/// <summary>
/// 本地数据目录约定：%LOCALAPPDATA%\CloudFlow\
/// </summary>
public static class CloudFlowPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudFlow");

    public static string AuditFile => Path.Combine(Root, "audit.jsonl");

    public static string SavedScopesFile => Path.Combine(Root, "saved-scopes.json");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }
}
