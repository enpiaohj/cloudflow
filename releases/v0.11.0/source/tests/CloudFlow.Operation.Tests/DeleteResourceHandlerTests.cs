using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// <see cref="DeleteResourceHandler"/> 的边界测试：虚拟机类型拒绝走这条通用删除路径，
/// 以及 Execute 前的存在性预检查（目标已不存在时视为已达成，不撞 404）。
/// </summary>
public sealed class DeleteResourceHandlerTests
{
    [Fact]
    public async Task ValidateAsync_目标是虚拟机类型时拒绝()
    {
        var handler = new DeleteResourceHandler(new RecordingExecutor());
        var request = Request(
            resourceId: "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/vm1",
            resourceType: "microsoft.compute/virtualmachines");

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ValidateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_非法ResourceId时拒绝()
    {
        var handler = new DeleteResourceHandler(new RecordingExecutor());
        var request = Request(resourceId: "not-a-resource-id");

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ValidateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_目标已不存在时直接返回不调用Delete()
    {
        var executor = new RecordingExecutor { Exists = false };
        var handler = new DeleteResourceHandler(executor);

        var requestId = await handler.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Null(requestId);
        Assert.False(executor.DeleteCalled);
    }

    [Fact]
    public async Task ExecuteAsync_目标仍存在时调用Delete并返回其结果()
    {
        var executor = new RecordingExecutor { Exists = true };
        var handler = new DeleteResourceHandler(executor);

        var requestId = await handler.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal("request-123", requestId);
        Assert.True(executor.DeleteCalled);
    }

    [Fact]
    public async Task VerifyAsync_目标已消失时判定为成功()
    {
        var executor = new RecordingExecutor { Exists = false };
        var handler = new DeleteResourceHandler(executor);

        var verified = await handler.VerifyAsync(Request(), "req-1", CancellationToken.None);

        Assert.True(verified);
    }

    private static OperationRequest Request(string? resourceId = null, string? resourceType = null)
    {
        var payload = new Dictionary<string, string>();
        if (resourceType is not null)
        {
            payload[GenericResourceModule.PayloadResourceType] = resourceType;
        }

        return new OperationRequest
        {
            OperationType = GenericResourceModule.OperationDelete,
            AccountId = "account-1",
            TenantId = "tenant-1",
            SubscriptionId = "sub-1",
            ResourceId = resourceId ??
                "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Network/publicIPAddresses/ip1",
            Display = "删除资源 ip1",
            Payload = payload
        };
    }

    private sealed class RecordingExecutor : IResourceDeleteExecutor
    {
        public bool Exists { get; set; } = true;

        public bool DeleteCalled { get; private set; }

        public Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default)
        {
            DeleteCalled = true;
            return Task.FromResult<string?>("request-123");
        }

        public Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Exists);
    }
}
