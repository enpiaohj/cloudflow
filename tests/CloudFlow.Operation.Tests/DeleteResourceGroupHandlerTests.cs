using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// <see cref="DeleteResourceGroupHandler"/> 的安全边界测试：删除资源组是级联删除，
/// 组内有虚拟机时必须在 Impact 描述里点名，不能让用户以为只是清理网络残留。
/// </summary>
public sealed class DeleteResourceGroupHandlerTests
{
    [Fact]
    public async Task AnalyzeImpactAsync_组内含虚拟机时描述必须点名且不可绕过审批()
    {
        var executor = new RecordingExecutor
        {
            Contained =
            [
                new ResourceSummary(
                    "sgtest07", "Microsoft.Compute/virtualMachines",
                    "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/sgtest07"),
                new ResourceSummary(
                    "sgtest07-vnet", "Microsoft.Network/virtualNetworks",
                    "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Network/virtualNetworks/sgtest07-vnet")
            ]
        };
        var handler = new DeleteResourceGroupHandler(executor);

        var impact = await handler.AnalyzeImpactAsync(Request(), CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        Assert.True(impact.CannotBypass);
        Assert.Equal(2, impact.AffectedResources);
        Assert.Contains("sgtest07", impact.Description, StringComparison.Ordinal);
        Assert.Contains("虚拟机", impact.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeImpactAsync_空资源组正常走完且不提虚拟机()
    {
        var executor = new RecordingExecutor { Contained = [] };
        var handler = new DeleteResourceGroupHandler(executor);

        var impact = await handler.AnalyzeImpactAsync(Request(), CancellationToken.None);

        Assert.Equal(0, impact.AffectedResources);
        Assert.Contains("为空", impact.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("虚拟机", impact.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_资源组名称不合法时拒绝()
    {
        var executor = new RecordingExecutor();
        var handler = new DeleteResourceGroupHandler(executor);
        var badRequest = Request(resourceId:
            "/subscriptions/sub-1/resourceGroups/bad*name");

        await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ValidateAsync(badRequest, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_资源组已不存在时直接返回不调用Delete()
    {
        var executor = new RecordingExecutor { Exists = false };
        var handler = new DeleteResourceGroupHandler(executor);

        var requestId = await handler.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Null(requestId);
        Assert.False(executor.DeleteCalled);
    }

    [Fact]
    public async Task ExecuteAsync_资源组仍存在时调用Delete并返回其结果()
    {
        var executor = new RecordingExecutor { Exists = true };
        var handler = new DeleteResourceGroupHandler(executor);

        var requestId = await handler.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal("request-123", requestId);
        Assert.True(executor.DeleteCalled);
    }

    [Fact]
    public async Task VerifyAsync_资源组已消失时判定为成功()
    {
        var executor = new RecordingExecutor { Exists = false };
        var handler = new DeleteResourceGroupHandler(executor);

        var verified = await handler.VerifyAsync(Request(), "req-1", CancellationToken.None);

        Assert.True(verified);
    }

    [Fact]
    public async Task VerifyAsync_资源组仍存在时判定为失败()
    {
        var executor = new RecordingExecutor { Exists = true };
        var handler = new DeleteResourceGroupHandler(executor);

        var verified = await handler.VerifyAsync(Request(), "req-1", CancellationToken.None);

        Assert.False(verified);
    }

    private static OperationRequest Request(string? resourceId = null) => new()
    {
        OperationType = ResourceGroupModule.OperationDelete,
        AccountId = "account-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = resourceId ?? "/subscriptions/sub-1/resourceGroups/rg-test",
        Display = "删除资源组 rg-test"
    };

    private sealed class RecordingExecutor : IResourceGroupDeleteExecutor
    {
        public IReadOnlyList<ResourceSummary> Contained { get; set; } = [];

        public bool Exists { get; set; } = true;

        public bool DeleteCalled { get; private set; }

        public Task<IReadOnlyList<ResourceSummary>> GetContainedResourcesAsync(
            OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Contained);

        public Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default)
        {
            DeleteCalled = true;
            return Task.FromResult<string?>("request-123");
        }

        public Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Exists);
    }
}
