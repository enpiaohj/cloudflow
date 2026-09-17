using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

/// <summary>
/// 挂起审批的落盘。Job 是落盘的、挂起上下文曾经只在内存里 ——
/// 两者错位的结果是重启后 Job 卡在 WaitingApproval 且永远批不了。
/// 这一组测的就是"真的写进文件、换个实例读回来"。
/// </summary>
public sealed class JsonJobStorePendingRequestTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "jobs.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OperationRequest Request() => new()
    {
        OperationType = "network.change_port",
        AccountId = "azurecli:profile-1",
        TenantId = "tenant-1",
        AccountDisplayName = "Piao Hongji",
        SubscriptionId = "sub-1",
        ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
        ProviderProfileId = "profile-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/networkSecurityGroups/nsg1",
        Risk = RiskLevel.Medium,
        Payload = new Dictionary<string, string>
        {
            ["ruleId"] = "nsg1/AllowSSH",
            ["port"] = "2222"
        },
        Display = "更改端口 AllowSSH：22 → 2222"
    };

    private static OperationJob WaitingJob(OperationRequest request) => new()
    {
        AccountId = request.AccountId,
        TenantId = request.TenantId,
        SubscriptionId = request.SubscriptionId,
        ProviderType = request.ProviderType,
        ProviderProfileId = request.ProviderProfileId,
        ResourceId = request.ResourceId,
        OperationType = request.OperationType,
        Display = request.Display,
        Risk = request.Risk,
        Status = JobStatus.WaitingApproval,
        Summary = "共享网络安全组：会波及同子网 3 台虚拟机。",
        ImpactAffectedResources = 3,
        PendingRequest = request
    };

    [Fact]
    public async Task 待审批请求随Job一起落盘且能读回()
    {
        var job = WaitingJob(Request());
        await new JsonJobStore(FilePath).AddAsync(job);

        var restored = new JsonJobStore(FilePath).Find(job.JobId);

        Assert.NotNull(restored);
        Assert.Equal(JobStatus.WaitingApproval, restored!.Status);
        Assert.NotNull(restored.PendingRequest);
        Assert.Equal("network.change_port", restored.PendingRequest!.OperationType);
        Assert.Equal("sub-1", restored.PendingRequest.SubscriptionId);
        Assert.Equal("2222", restored.PendingRequest.Payload["port"]);
        // 身份上下文必须完整：重启后要凭它去取对应账户的凭据
        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, restored.PendingRequest.ProviderType);
        Assert.Equal("profile-1", restored.PendingRequest.ProviderProfileId);
    }

    [Fact]
    public async Task Job的其余字段不因为多了待审批请求而丢()
    {
        var job = WaitingJob(Request());
        await new JsonJobStore(FilePath).AddAsync(job);

        var restored = new JsonJobStore(FilePath).Find(job.JobId)!;

        Assert.Equal(job.CorrelationId, restored.CorrelationId);
        Assert.Equal(3, restored.ImpactAffectedResources);
        Assert.Equal("共享网络安全组：会波及同子网 3 台虚拟机。", restored.Summary);
    }

    [Fact]
    public async Task 旧版本写下的WaitingApproval_读回来没有待审批请求()
    {
        // 模拟旧版 jobs.json：没有 pendingRequest 字段。这不该解析失败，
        // 只该得到一个"批不了"的 Job —— UI 据此显示重新提交，而不是让用户点下去才报错。
        Directory.CreateDirectory(_directory);
        var legacyJobId = Guid.NewGuid();
        File.WriteAllText(FilePath, $$"""
        [{
          "JobId": "{{legacyJobId}}",
          "AccountId": "acc-1",
          "TenantId": "tenant-1",
          "SubscriptionId": "sub-1",
          "ResourceId": "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
          "OperationType": "vm.restart",
          "Status": "WaitingApproval",
          "Risk": "High"
        }]
        """);

        var restored = new JsonJobStore(FilePath).Find(legacyJobId);

        Assert.NotNull(restored);
        Assert.Equal(JobStatus.WaitingApproval, restored!.Status);
        Assert.Null(restored.PendingRequest);
    }

    [Fact]
    public async Task 落盘的待审批请求不含任何凭据字段()
    {
        // 安全断言：OperationRequest 能落盘的前提是它只装 string / enum / bool / 字符串字典。
        // 哪天有人往请求里加了 Token 或凭据字段，这条会失败 —— 那是必须被拦下的改动，
        // 而不是等到某天有人在 jobs.json 里发现一个 Bearer Token。
        await new JsonJobStore(FilePath).AddAsync(WaitingJob(Request()));

        var json = File.ReadAllText(FilePath);

        foreach (var forbidden in new[]
                 {
                     "token", "secret", "password", "credential", "apikey", "api_key",
                     "refresh", "assertion", "clientsecret", "accesstoken"
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 批准后清空请求_再落盘时文件里不再有它()
    {
        var store = new JsonJobStore(FilePath);
        var job = WaitingJob(Request());
        await store.AddAsync(job);

        job.PendingRequest = null;
        job.Status = JobStatus.Succeeded;
        await store.UpdateAsync(job);

        var json = File.ReadAllText(FilePath);

        // Job 自己带着 OperationType、Display 里也有端口号，所以都不能当"请求还在"的判据；
        // 判据是那个字段本身，以及只存在于请求里的 Payload
        Assert.DoesNotContain("pendingRequest", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Payload", json);
    }
}
