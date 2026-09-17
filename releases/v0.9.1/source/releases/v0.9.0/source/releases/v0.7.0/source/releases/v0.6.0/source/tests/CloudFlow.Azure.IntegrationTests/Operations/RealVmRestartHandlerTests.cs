using Azure.ResourceManager;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Compute;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Operations.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Operations;

/// <summary>
/// 真实重启链路：OperationEngine → RestartVmHandler → ArmVmPowerExecutor → IAzureClientFactory → ARM LRO。
///
/// 四项语义都必须保住，尤其"审批前不创建 ARM Client" —— 它证明审批门确实挡住了执行，
/// 而不是先打了 Azure 再补一句"等待审批"。
/// </summary>
public sealed class RealVmRestartHandlerTests
{
    // 订阅必须是 GUID：真实 Azure Resource ID 一律如此，而 ResourceIdentifier 也据此校验。
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";

    private const string ResourceId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-1";

    private sealed class ThrowingClientFactory : IAzureClientFactory
    {
        public int CreateCalls { get; private set; }

        public Task<ArmClient> CreateAsync(
            CloudCredentialContext context,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            throw new InvalidOperationException("测试替身不创建 ARM Client。");
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public Task WriteAsync(AuditRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<AuditRecord> Query(string? resourceId = null, int max = 200) => [];
    }

    private static OperationRequest Request(string? resourceId = null) => new()
    {
        OperationType = ComputeModule.OperationRestart,
        AccountId = "account-1",
        TenantId = "22222222-2222-2222-2222-222222222222",
        SubscriptionId = SubscriptionId,
        ProviderType = AuthenticationProviderType.EntraMsal,
        ResourceId = resourceId ?? ResourceId,
        Payload = new Dictionary<string, string>()
    };

    private static RestartVmHandler Handler(IAzureClientFactory factory) =>
        new(new ArmVmPowerExecutor(factory, NullLogger<ArmVmPowerExecutor>.Instance),
            NullLogger<RestartVmHandler>.Instance);

    [Fact]
    public async Task ValidateAsync_审批前不触碰Azure()
    {
        var factory = new ThrowingClientFactory();

        await Handler(factory).ValidateAsync(Request(), CancellationToken.None);

        // 测试替身一旦被调用就会抛错，能跑到这里就说明 Validate 完全没有发起 ARM 调用。
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task ValidateAsync_缺少Provider时拒绝执行()
    {
        var request = new OperationRequest
        {
            OperationType = ComputeModule.OperationRestart,
            AccountId = "account-1",
            TenantId = "22222222-2222-2222-2222-222222222222",
            SubscriptionId = SubscriptionId,
            ResourceId = ResourceId,
            Payload = new Dictionary<string, string>()
        };

        await Assert.ThrowsAsync<OperationValidationException>(() =>
            Handler(new ThrowingClientFactory()).ExecuteAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeImpactAsync_始终要求单次审批()
    {
        var impact = await Handler(new ThrowingClientFactory())
            .AnalyzeImpactAsync(Request(), CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        Assert.Equal(1, impact.AffectedResources);
        Assert.Contains("vm-1", impact.Description);
    }

    [Fact]
    public async Task 提交后仅等待审批_审批前不创建ArmClient()
    {
        var factory = new ThrowingClientFactory();
        var engine = new OperationEngine(
            [Handler(factory)],
            new InMemoryJobStore(),
            new RecordingAudit(),
            NullLogger<OperationEngine>.Instance);

        var pending = await engine.SubmitAsync(Request());

        Assert.Equal(JobStatus.WaitingApproval, pending.Status);
        Assert.Equal(0, factory.CreateCalls);

        var result = await engine.ApproveAsync(pending.JobId);

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal(1, factory.CreateCalls);
    }
}
