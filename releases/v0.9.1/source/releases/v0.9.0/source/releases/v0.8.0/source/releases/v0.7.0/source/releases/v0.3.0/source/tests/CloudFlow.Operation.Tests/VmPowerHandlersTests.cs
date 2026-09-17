using CloudFlow.Core.Errors;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// <see cref="DeallocateVmHandler"/> 的状态前置条件回归测试。
/// 真实 bug：之前要求"必须先是 Running 才能解除分配"，但 Azure 本身允许对已经 Stopped
/// （关机但仍占用计算资源）的虚拟机直接解除分配，不需要先重新启动——用户在真实账户上
/// 复现过这个报错。
/// </summary>
public sealed class VmPowerHandlersTests
{
    [Theory]
    [InlineData(VmPowerState.Running)]
    [InlineData(VmPowerState.Stopped)]
    public async Task DeallocateVmHandler_Running或Stopped都允许执行(VmPowerState current)
    {
        var executor = new RecordingPowerExecutor { PowerState = current };
        var handler = new DeallocateVmHandler(executor, NullLogger<DeallocateVmHandler>.Instance);

        var requestId = await handler.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal("request-123", requestId);
        Assert.Equal(VmPowerAction.Deallocate, executor.LastAction);
    }

    [Fact]
    public async Task DeallocateVmHandler_已经解除分配时拒绝重复执行()
    {
        var executor = new RecordingPowerExecutor { PowerState = VmPowerState.Deallocated };
        var handler = new DeallocateVmHandler(executor, NullLogger<DeallocateVmHandler>.Instance);

        var error = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ExecuteAsync(Request(), CancellationToken.None));

        Assert.Contains("已解除分配", error.Message, StringComparison.Ordinal);
    }

    private static OperationRequest Request() => new()
    {
        OperationType = ComputeModule.OperationDeallocate,
        AccountId = "account-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ProviderType = AuthenticationProviderType.EntraMsal,
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/vm-1"
    };

    private sealed class RecordingPowerExecutor : IVmPowerExecutor
    {
        public VmPowerState? PowerState { get; set; }

        public VmPowerAction? LastAction { get; private set; }

        public Task<VmPowerState?> GetPowerStateAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(PowerState);

        public Task<string?> GetVmSizeAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> ExecuteAsync(
            OperationRequest request, VmPowerAction action, CancellationToken ct = default)
        {
            LastAction = action;
            return Task.FromResult<string?>("request-123");
        }

        public Task<string?> ResizeAsync(OperationRequest request, string newSize, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
