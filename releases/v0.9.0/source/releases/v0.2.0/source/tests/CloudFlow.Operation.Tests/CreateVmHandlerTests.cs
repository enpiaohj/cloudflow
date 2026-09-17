using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudFlow.Operation.Tests;

/// <summary>
/// <see cref="CreateVmHandler"/> 的安全边界测试。
/// 密码字段会随 PendingRequest 落盘，必须在 Validate 阶段拒绝，而不是只靠 UI 不去填写。
/// </summary>
public sealed class CreateVmHandlerTests
{
    [Fact]
    public async Task ValidateAsync_密码明文进入载荷时拒绝请求()
    {
        var payload = ValidPayload();
        payload["adminPassword"] = "must-not-be-persisted";
        var handler = CreateHandler();

        var error = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ValidateAsync(Request(payload), CancellationToken.None));

        Assert.Contains("credentialId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_Windows镜像与SSH公钥组合被拒绝()
    {
        var payload = ValidPayload();
        payload[CreateVmHandler.PayloadImage] = "MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure:latest";
        var handler = CreateHandler();

        var error = await Assert.ThrowsAsync<OperationValidationException>(
            () => handler.ValidateAsync(Request(payload), CancellationToken.None));

        Assert.Contains("Windows", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeImpactAsync_公网IP创建正确表达审批与资源数量()
    {
        var payload = ValidPayload();
        payload[CreateVmHandler.PayloadPublicIp] = "true";
        var handler = CreateHandler();

        await handler.ValidateAsync(Request(payload), CancellationToken.None);
        var impact = await handler.AnalyzeImpactAsync(Request(payload), CancellationToken.None);

        Assert.True(impact.RequiresApproval);
        Assert.False(impact.CannotBypass);
        Assert.Equal(4, impact.AffectedResources);
        Assert.Contains("公网 IP", impact.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_通过执行器且不传入密码解析委托()
    {
        var executor = new RecordingProvisioningExecutor();
        var handler = new CreateVmHandler(executor, NullLogger<CreateVmHandler>.Instance);
        var request = Request(ValidPayload());

        var requestId = await handler.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("request-123", requestId);
        Assert.Same(request, executor.LastRequest);
        Assert.Null(executor.LastPasswordResolver);
    }

    private static CreateVmHandler CreateHandler() =>
        new(new RecordingProvisioningExecutor(), NullLogger<CreateVmHandler>.Instance);

    private static Dictionary<string, string> ValidPayload() => new(StringComparer.OrdinalIgnoreCase)
    {
        [CreateVmHandler.PayloadVmName] = "cf-test-vm",
        [CreateVmHandler.PayloadRegion] = "koreacentral",
        [CreateVmHandler.PayloadSubnetId] = "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Network/virtualNetworks/vnet-test/subnets/snet-test",
        [CreateVmHandler.PayloadVmSize] = "Standard_B1s",
        [CreateVmHandler.PayloadImage] = "Canonical:ubuntu-24_04-lts:server:latest",
        [CreateVmHandler.PayloadAdminUsername] = "azureuser",
        [CreateVmHandler.PayloadAuthType] = "ssh",
        [CreateVmHandler.PayloadSshPublicKey] = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample cloudflow-test",
        [CreateVmHandler.PayloadPublicIp] = "false"
    };

    private static OperationRequest Request(IReadOnlyDictionary<string, string> payload) => new()
    {
        OperationType = ComputeModule.OperationCreate,
        AccountId = "account-1",
        TenantId = "tenant-1",
        SubscriptionId = "sub-1",
        ResourceId = "/subscriptions/sub-1/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/cf-test-vm",
        Display = "创建虚拟机 cf-test-vm",
        Payload = payload
    };

    private sealed class RecordingProvisioningExecutor : IVmProvisioningExecutor
    {
        public OperationRequest? LastRequest { get; private set; }
        public Func<CancellationToken, Task<string?>>? LastPasswordResolver { get; private set; }

        public Task<string?> CreateAsync(
            OperationRequest request,
            Func<CancellationToken, Task<string?>>? resolvePassword,
            CancellationToken ct = default)
        {
            LastRequest = request;
            LastPasswordResolver = resolvePassword;
            return Task.FromResult<string?>("request-123");
        }

        public Task<bool> VmReadyAsync(OperationRequest request, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
