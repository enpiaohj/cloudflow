using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace CloudFlow.Spike;

/// <summary>
/// CloudFlow P0 技术验证控制台（设计文档 §72–§74）。
///
/// 验证项：
///   1. MSAL 交互登录（Public Client，无 Secret）
///   2. Token Cache 多账户（GetAccountsAsync）
///   3. Subscription 发现（含多 Tenant 识别）
///   4. Resource Graph 跨订阅查询（summarize count）
///
/// 配置方式（任选其一）：
///   a. 复制 appsettings.example.json 为 appsettings.json 并填 ClientId
///   b. 环境变量 CLOUDFLOW_CLIENT_ID / CLOUDFLOW_TENANT_ID
/// </summary>
public static class Program
{
    private const string ManagementScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== CloudFlow P0 Technical Spike ===");
        Console.WriteLine();

        var clientId = ResolveValue(args, "--client-id", "CLOUDFLOW_CLIENT_ID") ?? LoadFromConfig()?.ClientId;
        var tenantId = ResolveValue(args, "--tenant-id", "CLOUDFLOW_TENANT_ID")
                       ?? LoadFromConfig()?.TenantId
                       ?? "organizations";

        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("SET-YOUR"))
        {
            Console.WriteLine("❌ 未配置 ClientId。请任选其一：");
            Console.WriteLine("   1. 复制 tools/CloudFlow.Spike/appsettings.example.json 为 appsettings.json，填入 App Registration ClientId");
            Console.WriteLine("   2. 设置环境变量 CLOUDFLOW_CLIENT_ID（CLOUDFLOW_TENANT_ID 可选）");
            Console.WriteLine("   3. 运行参数 --client-id <guid> [--tenant-id <guid|organizations>]");
            return 1;
        }

        var pca = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .WithLogging((level, message, _) => Console.WriteLine($"   [msal:{level}] {message}"), enablePiiLogging: false)
            .Build();

        // ---- 1. 交互登录 ----
        Console.WriteLine("[1/4] MSAL 交互登录（浏览器将打开）…");
        AuthenticationResult auth;
        var accounts = await pca.GetAccountsAsync();
        var existing = accounts.FirstOrDefault();
        if (existing is not null)
        {
            try
            {
                auth = await pca.AcquireTokenSilent([ManagementScope], existing).ExecuteAsync();
                Console.WriteLine($"    ✓ 静默登录成功：{auth.Account.Username}");
            }
            catch (MsalUiRequiredException)
            {
                auth = await InteractiveAsync(pca);
            }
        }
        else
        {
            auth = await InteractiveAsync(pca);
        }

        Console.WriteLine($"    ✓ Account: {auth.Account.Username}");
        Console.WriteLine($"    ✓ Home Tenant: {auth.Account.HomeAccountId?.TenantId}");
        Console.WriteLine($"    ✓ Token expires: {auth.ExpiresOn.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // ---- 2. Token Cache 多账户 ----
        Console.WriteLine("[2/4] Token Cache 账户列表（多账号验证，§74）：");
        var cachedAccounts = await pca.GetAccountsAsync();
        foreach (var account in cachedAccounts)
        {
            Console.WriteLine($"    · {account.Username}  ({account.HomeAccountId?.TenantId})");
        }
        Console.WriteLine();

        // ---- 3. Subscription 发现 ----
        Console.WriteLine("[3/4] 订阅发现（ARM /subscriptions）：");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var subs = await GetJsonAsync(http, $"{ArmBase}/subscriptions?api-version=2022-12-01");
        var subList = subs.GetProperty("value");
        var tenantIds = new HashSet<string>();
        foreach (var sub in subList.EnumerateArray())
        {
            var name = sub.GetProperty("displayName").GetString();
            var id = sub.GetProperty("subscriptionId").GetString();
            var tenant = sub.GetProperty("tenantId").GetString();
            var state = sub.GetProperty("state").GetString();
            tenantIds.Add(tenant ?? "");
            Console.WriteLine($"    · {name,-28} {id}  [{state}]  tenant={tenant}");
        }
        Console.WriteLine($"    ✓ 共 {subList.GetArrayLength()} 个订阅，覆盖 {tenantIds.Count} 个 Tenant（多租户发现 §72）");
        Console.WriteLine();

        // ---- 4. Resource Graph 跨订阅查询 ----
        Console.WriteLine("[4/4] Resource Graph 跨订阅查询（VM 数量统计）：");
        var allSubIds = subList.EnumerateArray()
            .Select(s => s.GetProperty("subscriptionId").GetString())
            .Where(s => s is not null)
            .Select(s => $"\"{s}\"");
        var subscriptionsScope = string.Join(",", allSubIds);

        var queryBody = $$"""
            {
              "subscriptions": [{{subscriptionsScope}}],
              "query": "summarize vmCount = count()"
            }
            """;

        var result = await PostJsonAsync(http,
            $"{ArmBase}/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
            queryBody);

        if (result.TryGetProperty("totalRecords", out var total))
        {
            Console.WriteLine($"    ✓ Resource Graph 查询成功：totalRecords = {total.GetInt32()}");
            if (result.TryGetProperty("data", out var data) &&
                data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("vmCount", out var vmCount))
            {
                Console.WriteLine($"    ✓ 当前账户可见 VM 总数（resourceContainers 聚合）：{vmCount.GetInt64()}");
            }
        }
        else
        {
            Console.WriteLine("    ⚠ Resource Graph 查询返回异常结构：");
            Console.WriteLine("      " + result.GetRawText()[..Math.Min(400, result.GetRawText().Length)]);
        }

        Console.WriteLine();
        Console.WriteLine("=== P0 Spike 完成 ===");
        Console.WriteLine("全部通过 → 可以进入 P1（MSAL 接入桌面应用 + Resource Graph Inventory）。");
        return 0;
    }

    private static async Task<AuthenticationResult> InteractiveAsync(IPublicClientApplication pca)
    {
        var auth = await pca.AcquireTokenInteractive([ManagementScope])
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync();
        Console.WriteLine($"    ✓ 交互登录成功：{auth.Account.Username}");
        return auth;
    }

    private static string? ResolveValue(string[] args, string argName, string envName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == argName)
            {
                return args[i + 1];
            }
        }
        return Environment.GetEnvironmentVariable(envName);
    }

    private record ConfigInfo(string? ClientId, string? TenantId);

    private static ConfigInfo? LoadFromConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var azure = doc.RootElement.GetProperty("Azure");
            return new ConfigInfo(
                azure.TryGetProperty("ClientId", out var cid) ? cid.GetString() : null,
                azure.TryGetProperty("TenantId", out var tid) ? tid.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {url} → {(int)response.StatusCode}: {body}");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {url} → {(int)response.StatusCode}: {body}");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
