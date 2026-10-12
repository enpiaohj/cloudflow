using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudFlow.Azure.Auth;

/// <summary>
/// 把登录服务配置（应用（客户端）ID、目录（租户））写入本机用户配置文件
/// （%LOCALAPPDATA%\CloudFlow\<see cref="MsalAuthConfig.UserConfigFileName"/>）。
/// </summary>
/// <remarks>
/// 单文件发布包里没有 appsettings.json，也不该有——真实 ClientId 不随发布产物分发（v0.2.0 起的约定），
/// 所以由用户在设置页填写一次，写到这里，启动时与程序目录的 appsettings.json 一起读取（这里优先）。
/// 只保存公开的客户端 ID 与租户，不含任何密钥；文件里已有的其它配置项原样保留。
/// </remarks>
public static class MsalAuthConfigFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <exception cref="ArgumentException">客户端 ID 或租户格式不合法。</exception>
    public static void Save(string path, string clientId, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!MsalAuthConfig.IsValidClientId(clientId))
        {
            throw new ArgumentException("应用（客户端）ID 必须是 GUID。", nameof(clientId));
        }

        if (!MsalAuthConfig.IsValidTenant(tenantId))
        {
            throw new ArgumentException(
                "目录（租户）必须是 organizations、common、目录（租户）ID 或已验证的域名。", nameof(tenantId));
        }

        var root = Load(path);
        if (root[MsalAuthConfig.SectionName] is not JsonObject azure)
        {
            azure = new JsonObject();
            root[MsalAuthConfig.SectionName] = azure;
        }

        azure[nameof(MsalAuthConfig.ClientId)] = clientId.Trim();
        azure[nameof(MsalAuthConfig.TenantId)] = tenantId.Trim();

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // 先写临时文件再替换：写到一半中断不会留下半截 JSON（那样下次启动整份配置都读不出来）。
        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented));
        File.Move(temp, fullPath, overwrite: true);
    }

    /// <summary>
    /// 已有文件解析失败时从空对象开始：这个文件只由本类写入，损坏的内容没有可保留的价值，
    /// 而拒绝保存会让用户永远配置不上。
    /// </summary>
    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
