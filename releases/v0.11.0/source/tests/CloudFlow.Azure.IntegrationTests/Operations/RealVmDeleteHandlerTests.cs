using Azure.ResourceManager;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Compute;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Operations.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Operations;

/// <summary>
/// 删除链路（设计文档 v3.1 §87）：OperationEngine → DeleteVmHandler → IVmDeleteExecutor → ARM LRO。
///
/// 删除是这个项目里唯一<b>不可逆</b>的写操作，因此下面每条断言都对应一条必须成立的安全语义：
/// 审批前不碰 Azure、审批永远不可绕过、不信任请求里的资源、先删 VM 再删连带资源。
/// </summary>
public sealed class RealVmDeleteHandlerTests
{
    // 订阅必须是 GUID：真实 Azure Resource ID 一律如此，而 ResourceIdentifier 也据此校验。
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";

    private const string VmResourceId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-1";

    private const string NicId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Network/networkInterfaces/vm-1-nic";

    private const string OsDiskId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/disks/vm-1-osdisk";

    // ── 测试替身 ──────────────────────────────────────────────────

    /// <summary>
    /// 记录调用顺序、可配置"哪些连带资源删不掉"的假执行器。
    /// 用它才能断言「先删 VM 再删连带资源」与「只删真实存在的那几件」——
    /// 这两条如果藏在 ARM 执行器里，就只能靠真机验证了。
    /// </summary>
    private sealed class FakeExecutor : IVmDeleteExecutor
    {
        public List<VmLinkedResource> Linked { get; } = [];

        public bool VmExists { get; set; } = true;

        public HashSet<string> FailingLinkedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
            OperationRequest request, CancellationToken ct = default)
        {
            Calls.Add("get-linked");
            return Task.FromResult<IReadOnlyList<VmLinkedResource>>(VmExists ? Linked : []);
        }

        public Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default)
        {
            Calls.Add("delete-vm");
            VmExists = false;
            return Task.FromResult<string?>("req-1");
        }

        public Task<(bool Success, string? Reason)> DeleteLinkedAsync(
            OperationRequest request, VmLinkedResource resource, CancellationToken ct = default)
        {
            Calls.Add($"delete-linked:{resource.ResourceId}");
            var failing = FailingLinkedIds.Contains(resource.ResourceId);
            return Task.FromResult<(bool, string?)>((!failing, failing ? "模拟失败" : null));
        }

        public Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(VmExists);
    }

    private sealed class ThrowingClientFactory : IAzureClientFactory
    {
        public int CreateCalls { get; private set; }

        public Task<ArmClient> CreateAsync(
            CloudCredentialContext context, CancellationToken cancellationToken = default)
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

    // ── 夹具 ──────────────────────────────────────────────────────

    /// <summary>
    /// 请求构造后<b>不可改</b>（<c>Payload</c> 只读、<c>PreApproved</c> 是 init-only），
    /// 所以所有变体都在构造时给定，而不是造完再改字段。
    /// </summary>
    private static OperationRequest Request(
        IReadOnlyDictionary<string, string> payload, bool preApproved = false) => new()
    {
        OperationType = ComputeModule.OperationDelete,
        AccountId = "account-1",
        TenantId = "22222222-2222-2222-2222-222222222222",
        SubscriptionId = SubscriptionId,
        ProviderType = AuthenticationProviderType.EntraMsal,
        ResourceId = VmResourceId,
        PreApproved = preApproved,
        Payload = payload
    };

    private static Dictionary<string, string> Kinds(params VmLinkedResourceKind[] kinds) =>
        kinds.Length == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                [DeleteVmHandler.PayloadLinkedKinds] = string.Join(',', kinds.Select(kind => kind.ToString()))
            };

    private static OperationRequest Request(params VmLinkedResourceKind[] linkedKinds) =>
        Request(Kinds(linkedKinds));

    private static DeleteVmHandler Handler(IVmDeleteExecutor executor) =>
        new(executor, NullLogger<DeleteVmHandler>.Instance);

    private static DeleteVmHandler ArmHandler(IAzureClientFactory factory) =>
        new(new ArmVmDeleteExecutor(factory, NullLogger<ArmVmDeleteExecutor>.Instance),
            NullLogger<DeleteVmHandler>.Instance);

    private static FakeExecutor ExecutorWithNicAndOsDisk()
    {
        var executor = new FakeExecutor();
        executor.Linked.Add(new(NicId, VmLinkedResourceKind.NetworkInterface, "vm-1-nic（网卡）"));
        executor.Linked.Add(new(OsDiskId, VmLinkedResourceKind.OsDisk, "vm-1-osdisk（OS 磁盘）"));
        return executor;
    }

    // ── 审批前不碰 Azure ──────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_审批前不触碰Azure()
    {
        var factory = new ThrowingClientFactory();

        await ArmHandler(factory).ValidateAsync(
            Request(VmLinkedResourceKind.OsDisk), CancellationToken.None);

        // 测试替身一旦被调用就会抛错，能跑到这里就说明 Validate 完全没有发起 ARM 调用。
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task ValidateAsync_未知的连带删除类别被拒绝()
    {
        var request = Request(new Dictionary<string, string>
        {
            [DeleteVmHandler.PayloadLinkedKinds] = "NotAKind"
        });

        await Assert.ThrowsAsync<OperationValidationException>(() =>
            Handler(new FakeExecutor()).ValidateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task 提交后仅等待审批_连带资源被读过但一件事都没删()
    {
        // ⚠️ 与重启那条同名的测试**不能照抄**：删除的影响分析必须读回真实挂载关系才能算影响面，
        // 所以删除的「审批前不碰 Azure」只对**写操作**成立，不对读成立
        // （既有先例：共享子网 NSG 的影响面也是在 Handler 里现查的）。
        // 真正要守住的是「审批前一件事都没删掉」，下面就这么断言。
        var executor = ExecutorWithNicAndOsDisk();
        var engine = new OperationEngine(
            [Handler(executor)], new InMemoryJobStore(), new RecordingAudit(),
            NullLogger<OperationEngine>.Instance);

        var pending = await engine.SubmitAsync(Request(VmLinkedResourceKind.OsDisk));

        Assert.Equal(JobStatus.WaitingApproval, pending.Status);
        Assert.Contains("get-linked", executor.Calls);                       // Impact 读过（允许）
        Assert.DoesNotContain("delete-vm", executor.Calls);                  // 但没删
        Assert.DoesNotContain(executor.Calls, call => call.StartsWith("delete-linked:", StringComparison.Ordinal));
    }

    // ── 审批不可绕过（用户已定的硬要求）────────────────────────────

    [Fact]
    public async Task 即使已预先批准_删除仍然停在审批()
    {
        // 这条是用户明确要求的语义：删除不可逆，**即使用户把设置里的审批档调成「关闭」也照样拦**。
        // 引擎里能表达这件事的机制只有 CannotBypass —— 它不是"调用点记得传 false"那种约定。
        var request = Request(Kinds(VmLinkedResourceKind.OsDisk), preApproved: true);

        var impact = await Handler(ExecutorWithNicAndOsDisk())
            .AnalyzeImpactAsync(request, CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        Assert.True(impact.CannotBypass);

        // 端到端：提交一个已预先批准的请求，仍然必须停在 WaitingApproval
        var executor = ExecutorWithNicAndOsDisk();
        var engine = new OperationEngine(
            [Handler(executor)], new InMemoryJobStore(), new RecordingAudit(),
            NullLogger<OperationEngine>.Instance);

        var job = await engine.SubmitAsync(request);

        Assert.Equal(JobStatus.WaitingApproval, job.Status);
        Assert.DoesNotContain("delete-vm", executor.Calls);
    }

    [Fact]
    public async Task 影响面随勾选变化_未勾选的会明说保留()
    {
        var handler = Handler(ExecutorWithNicAndOsDisk());

        var none = await handler.AnalyzeImpactAsync(Request(), CancellationToken.None);
        var both = await handler.AnalyzeImpactAsync(
            Request(VmLinkedResourceKind.OsDisk, VmLinkedResourceKind.NetworkInterface),
            CancellationToken.None);

        // 只删 VM：影响面 1，且必须显式告知"剩下的还在计费"
        Assert.Equal(1, none.AffectedResources);
        Assert.Contains("继续计费", none.Description!);

        Assert.Equal(3, both.AffectedResources);
        Assert.Contains("vm-1-osdisk", both.Description!);
    }

    // ── 不信任请求、先删 VM 再删连带 ──────────────────────────────

    [Fact]
    public async Task 请求删一项该VM没有的资源时_不会去删任何东西()
    {
        // 载荷只表达"用户想删哪些类别"，不表达"允许删什么"。
        // 这台机器只有网卡与 OS 盘，请求却要删公网 IP —— 交集为空，因此一件都不该删。
        var executor = ExecutorWithNicAndOsDisk();

        await Handler(executor).ExecuteAsync(
            Request(VmLinkedResourceKind.PublicIpAddress), CancellationToken.None);

        Assert.DoesNotContain(executor.Calls, call => call.StartsWith("delete-linked:", StringComparison.Ordinal));

        // 反向保护：这台机器确实有可连带的资源，所以"一件都没删"不是因为本来就空
        Assert.Contains(executor.Calls, call => call == "get-linked");
    }

    [Fact]
    public async Task 执行顺序_先删虚拟机再删连带资源()
    {
        // 顺序不可颠倒：NIC 与磁盘附着在 VM 上时无法删除，反过来会拿到 OperationNotAllowed。
        var executor = ExecutorWithNicAndOsDisk();

        await Handler(executor).ExecuteAsync(
            Request(VmLinkedResourceKind.OsDisk, VmLinkedResourceKind.NetworkInterface),
            CancellationToken.None);

        var vmIndex = executor.Calls.IndexOf("delete-vm");
        var firstLinkedIndex = executor.Calls.FindIndex(
            call => call.StartsWith("delete-linked:", StringComparison.Ordinal));

        Assert.True(vmIndex >= 0, "没有删除虚拟机本身。");
        Assert.True(firstLinkedIndex > vmIndex, $"删连带资源发生在删 VM 之前：{string.Join(" → ", executor.Calls)}");
    }

    [Fact]
    public async Task 连带删除失败时_如实报失败并说明虚拟机已删()
    {
        var executor = ExecutorWithNicAndOsDisk();
        executor.FailingLinkedIds.Add(OsDiskId);

        var ex = await Assert.ThrowsAsync<CloudFlowException>(() =>
            Handler(executor).ExecuteAsync(
                Request(VmLinkedResourceKind.OsDisk, VmLinkedResourceKind.NetworkInterface),
                CancellationToken.None));

        // 必须说清"已经删了什么、还剩什么" —— 否则用户会以为整个操作都没生效；
        // 更不能静默吞掉，那些资源还在计费。
        Assert.Contains("虚拟机已删除", ex.Message);
        Assert.Contains("vm-1-osdisk", ex.Message);
    }

    [Fact]
    public async Task 虚拟机不存在时_Execute拒绝而不是当作成功()
    {
        var executor = ExecutorWithNicAndOsDisk();
        executor.VmExists = false;

        var ex = await Assert.ThrowsAsync<CloudFlowException>(() =>
            Handler(executor).ExecuteAsync(Request(), CancellationToken.None));

        Assert.Contains("未找到虚拟机", ex.Message);
    }

    // ── Verify ────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_虚拟机仍在时返回false()
    {
        var executor = ExecutorWithNicAndOsDisk();

        Assert.False(await Handler(executor).VerifyAsync(Request(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_虚拟机已消失时返回true()
    {
        var executor = ExecutorWithNicAndOsDisk();
        executor.VmExists = false;   // 模拟已删除

        Assert.True(await Handler(executor).VerifyAsync(
            Request(VmLinkedResourceKind.OsDisk), null, CancellationToken.None));
    }

    [Fact]
    public async Task 连带资源删不掉时_根本走不到Verify()
    {
        // Verify 刻意不校验连带资源：任何一件没删成功都会让 Execute 抛错、Job 直接 Failed。
        // 这条把那个前提锁住 —— 否则将来有人把 Execute 的失败改成静默忽略，
        // Verify 就变成了一道没有意义的空门。
        var executor = ExecutorWithNicAndOsDisk();
        executor.FailingLinkedIds.Add(OsDiskId);

        await Assert.ThrowsAsync<CloudFlowException>(() =>
            Handler(executor).ExecuteAsync(
                Request(VmLinkedResourceKind.OsDisk), CancellationToken.None));
    }
}
