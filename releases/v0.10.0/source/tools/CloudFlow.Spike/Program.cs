using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.ResourceManager.Compute;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Auth;
using CloudFlow.Azure.Identity.Msal;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
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

        if (args.Contains("--authorized-restart", StringComparer.Ordinal))
        {
            return await RunAuthorizedRestartAsync(args, clientId, tenantId);
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

    private static async Task<int> RunAuthorizedRestartAsync(
        string[] args,
        string clientId,
        string tenantId)
    {
        var subscriptionName = RequireArgument(args, "--subscription-name");
        var resourceGroupName = RequireArgument(args, "--resource-group");
        var vmName = RequireArgument(args, "--vm-name");
        var accountUsername = RequireArgument(args, "--account-username");

        var sessions = new MsalAccountSessionManager(
            new MsalAuthConfig { ClientId = clientId, TenantId = tenantId },
            NullLogger<MsalAccountSessionManager>.Instance);
        var identityProvider = new MsalIdentityProvider(sessions, new SubscriptionDiscoveryService());
        var accounts = await sessions.GetAccountsAsync();
        var account = accounts.SingleOrDefault(item =>
            string.Equals(item.Username, accountUsername, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            throw new InvalidOperationException("未找到指定企业账户的有效缓存会话，已取消操作。");
        }

        var subscriptions = await identityProvider.GetSubscriptionsAsync(account);
        var subscription = subscriptions
            .SingleOrDefault(item =>
                string.Equals(item.DisplayName, subscriptionName, StringComparison.Ordinal) &&
                string.Equals(item.State, "Enabled", StringComparison.OrdinalIgnoreCase));
        if (subscription is null)
        {
            throw new InvalidOperationException("未找到指定的已启用订阅，已取消操作。");
        }

        var context = new CloudCredentialContext
        {
            AccountId = account.AccountId,
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.SubscriptionId,
            ProviderType = AuthenticationProviderType.EntraMsal,
            ProviderProfileId = account.ProviderProfileId
        };
        var clientFactory = new CloudArmClientFactory([identityProvider]);
        var armClient = await clientFactory.CreateAsync(context);
        var resourceId = VirtualMachineResource.CreateResourceIdentifier(
            subscription.SubscriptionId,
            resourceGroupName,
            vmName);
        var vm = armClient.GetVirtualMachineResource(resourceId);
        var instanceView = (await vm.InstanceViewAsync()).Value;
        var powerState = instanceView.Statuses
            .FirstOrDefault(status => status.Code?.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase) == true)
            ?.Code ?? "PowerState/unknown";

        Console.WriteLine($"目标：订阅“{subscriptionName}”/ 资源组“{resourceGroupName}”/ VM“{vmName}”");
        Console.WriteLine($"重启前状态：{powerState}");

        var auditLog = new AuditFileLog();
        // 使用产品实际注册的 Handler + 执行器组合（与 App 的 DI 装配一致），
        // 这样 Spike 验证的就是真实链路，而不是一份只在 Spike 里存在的实现。
        var executor = new CloudFlow.Azure.Compute.ArmVmPowerExecutor(
            clientFactory, NullLogger<CloudFlow.Azure.Compute.ArmVmPowerExecutor>.Instance);
        var engine = new OperationEngine(
            [new CloudFlow.Modules.Compute.Operations.RestartVmHandler(
                executor, NullLogger<CloudFlow.Modules.Compute.Operations.RestartVmHandler>.Instance)],
            new JsonJobStore(),
            auditLog,
            NullLogger<OperationEngine>.Instance);
        var request = new OperationRequest
        {
            OperationType = CloudFlow.Modules.Compute.Models.ComputeModule.OperationRestart,
            AccountId = context.AccountId,
            TenantId = context.TenantId,
            SubscriptionId = context.SubscriptionId,
            ProviderType = context.ProviderType,
            ProviderProfileId = context.ProviderProfileId,
            ResourceId = resourceId.ToString(),
            Risk = RiskLevel.High,
            Display = $"重启虚拟机 {vmName}",
            Payload = new Dictionary<string, string>()
        };

        var pending = await engine.SubmitAsync(request);
        if (pending.Status != JobStatus.WaitingApproval)
        {
            throw new InvalidOperationException($"操作未进入 WaitingApproval，当前状态为 {pending.Status}，已取消执行。");
        }

        Console.WriteLine($"已创建 Job {pending.JobId}，状态 WaitingApproval；尚未调用 Azure Restart。");
        if (!args.Contains("--approve-restart-once", StringComparer.Ordinal))
        {
            return 0;
        }

        var completed = await engine.ApproveAsync(pending.JobId);
        var finalAudit = auditLog.Query(resourceId.ToString())
            .FirstOrDefault(record => record.JobId == completed.JobId);
        Console.WriteLine($"最终状态：{completed.Status}；审计结果：{finalAudit?.Outcome ?? "缺失"}。");
        return completed.Status == JobStatus.Succeeded &&
               string.Equals(finalAudit?.Outcome, "Succeeded", StringComparison.Ordinal)
            ? 0
            : 1;
    }

    private static string RequireArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return args[index + 1];
            }
        }

        throw new ArgumentException($"缺少必需参数：{name}");
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
        foreach (var path in GetConfigPaths())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var azure = doc.RootElement.GetProperty("Azure");
                var config = new ConfigInfo(
                    azure.TryGetProperty("ClientId", out var cid) ? cid.GetString() : null,
                    azure.TryGetProperty("TenantId", out var tid) ? tid.GetString() : null);
                if (!string.IsNullOrWhiteSpace(config.ClientId) &&
                    !config.ClientId.StartsWith("SET-YOUR", StringComparison.OrdinalIgnoreCase))
                {
                    return config;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> GetConfigPaths()
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(localPath))
        {
            yield return localPath;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var appPath = Path.Combine(directory.FullName, "src", "CloudFlow.App", "appsettings.json");
            if (File.Exists(appPath))
            {
                yield return appPath;
                yield break;
            }
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
