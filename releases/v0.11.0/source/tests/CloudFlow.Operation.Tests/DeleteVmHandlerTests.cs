using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// <see cref="DeleteVmHandler"/> 的连带资源清理行为测试。
/// 核心回归点：某一件连带资源删除失败，不能让排在它后面、本该删除的资源也没机会被尝试——
/// 这正是"删除虚拟机留下残留资源"的真实成因（见 <see cref="IVmDeleteExecutor.DeleteLinkedAsync"/>
/// 的实现方要求捕获所有非取消异常，而不是只捕获 <c>RequestFailedException</c>）。
/// </summary>
public sealed class DeleteVmHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_某个连带资源失败不影响后续资源仍被尝试删除()
    {
        var linked = new[]
        {
            new VmLinkedResource("/rid/nic", VmLinkedResourceKind.NetworkInterface, "cf-test-vm-nic（网卡）"),
            new VmLinkedResource("/rid/osdisk", VmLinkedResourceKind.OsDisk, "cf-test-vm-osdisk（OS 磁盘）"),
            new VmLinkedResource("/rid/ip", VmLinkedResourceKind.PublicIpAddress, "cf-test-vm-ip（公网 IP）")
        };
        var executor = new RecordingDeleteExecutor
        {
            Linked = linked,
            // 只让第一件（网卡）失败，模拟"非 ARM 异常"场景下也应该继续尝试后面的资源。
            Outcomes = { ["/rid/nic"] = (false, "连接超时") }
        };
        var handler = CreateHandler(executor);
        var payload = RequestedKindsPayload(
            VmLinkedResourceKind.NetworkInterface, VmLinkedResourceKind.OsDisk, VmLinkedResourceKind.PublicIpAddress);

        var error = await Assert.ThrowsAsync<CloudFlowException>(
            () => handler.ExecuteAsync(Request(payload), CancellationToken.None));

        // 三件都必须被尝试过一次——如果第一件失败就中断循环，这里只会看到 1 次调用。
        Assert.Equal(3, executor.AttemptedResourceIds.Count);
        Assert.Contains("/rid/nic", executor.AttemptedResourceIds);
        Assert.Contains("/rid/osdisk", executor.AttemptedResourceIds);
        Assert.Contains("/rid/ip", executor.AttemptedResourceIds);
        Assert.Contains("连接超时", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_多个连带资源失败时错误信息逐条列出资源名与原因()
    {
        var linked = new[]
        {
            new VmLinkedResource("/rid/nic", VmLinkedResourceKind.NetworkInterface, "cf-test-vm-nic（网卡）"),
            new VmLinkedResource("/rid/osdisk", VmLinkedResourceKind.OsDisk, "cf-test-vm-osdisk（OS 磁盘）")
        };
        var executor = new RecordingDeleteExecutor
        {
            Linked = linked,
            Outcomes =
            {
                ["/rid/nic"] = (false, "网卡仍被其它资源引用"),
                ["/rid/osdisk"] = (false, "磁盘正在被快照锁定")
            }
        };
        var handler = CreateHandler(executor);
        var payload = RequestedKindsPayload(VmLinkedResourceKind.NetworkInterface, VmLinkedResourceKind.OsDisk);

        var error = await Assert.ThrowsAsync<CloudFlowException>(
            () => handler.ExecuteAsync(Request(payload), CancellationToken.None));

        Assert.Contains("cf-test-vm-nic（网卡）（网卡仍被其它资源引用）", error.Message, StringComparison.Ordinal);
        Assert.Contains("cf-test-vm-osdisk（OS 磁盘）（磁盘正在被快照锁定）", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_全部连带资源成功删除时不抛异常()
    {
        var linked = new[]
        {
            new VmLinkedResource("/rid/nic", VmLinkedResourceKind.NetworkInterface, "cf-test-vm-nic（网卡）")
        };
        var executor = new RecordingDeleteExecutor { Linked = linked };
        var handler = CreateHandler(executor);
        var payload = RequestedKindsPayload(VmLinkedResourceKind.NetworkInterface);

        var requestId = await handler.ExecuteAsync(Request(payload), CancellationToken.None);

        Assert.Equal("request-123", requestId);
        Assert.Single(executor.AttemptedResourceIds);
    }

    private static DeleteVmHandler CreateHandler(IVmDeleteExecutor executor) =>
        new(executor, NullLogger<DeleteVmHandler>.Instance);

    private static Dictionary<string, string> RequestedKindsPayload(params VmLinkedResourceKind[] kinds) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [DeleteVmHandler.PayloadLinkedKinds] = string.Join(',', kinds.Select(k => k.ToString()))
        };

    private static OperationRequest Request(IReadOnlyDictionary<string, string> payload) => new()
    {
        OperationType = ComputeModule.OperationDelete,
        AccountId = "account-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/cf-test-vm",
        Display = "删除虚拟机 cf-test-vm",
        Payload = payload
    };

    private sealed class RecordingDeleteExecutor : IVmDeleteExecutor
    {
        public IReadOnlyList<VmLinkedResource> Linked { get; set; } = [];

        /// <summary>按 ResourceId 指定的删除结果；未指定的默认成功。</summary>
        public Dictionary<string, (bool Success, string? Reason)> Outcomes { get; } = [];

        public List<string> AttemptedResourceIds { get; } = [];

        public Task<IReadOnlyList<VmLinkedResource>> GetLinkedResourcesAsync(
            OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Linked);

        public Task<string?> DeleteVmAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult<string?>("request-123");

        public Task<(bool Success, string? Reason)> DeleteLinkedAsync(
            OperationRequest request, VmLinkedResource resource, CancellationToken ct = default)
        {
            AttemptedResourceIds.Add(resource.ResourceId);
            return Task.FromResult(
                Outcomes.TryGetValue(resource.ResourceId, out var outcome) ? outcome : (true, (string?)null));
        }

        public Task<bool> VmExistsAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
