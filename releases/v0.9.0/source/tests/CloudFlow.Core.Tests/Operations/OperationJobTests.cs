using CloudFlow.Core.Operations;
using Xunit;

namespace CloudFlow.Core.Tests.Operations;

public class OperationJobTests
{
    [Fact]
    public void OperationJob_创建_默认值完整()
    {
        var job = new OperationJob
        {
            AccountId = "acc",
            TenantId = "tenant",
            SubscriptionId = "sub",
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/VM01",
            OperationType = "vm.restart"
        };

        Assert.NotEqual(Guid.Empty, job.JobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.False(string.IsNullOrEmpty(job.CorrelationId));
        Assert.Equal(RiskLevel.Low, job.Risk);
    }

    [Fact]
    public void OperationRequest_负载与风险_可传递到Job()
    {
        var request = new OperationRequest
        {
            OperationType = "network.open_port",
            AccountId = "acc",
            TenantId = "tenant",
            SubscriptionId = "sub",
            ResourceId = "res",
            Risk = RiskLevel.High,
            PreApproved = false,
            Payload = new Dictionary<string, string> { ["port"] = "8443" }
        };

        Assert.Equal("8443", request.Payload["port"]);
        Assert.Equal(RiskLevel.High, request.Risk);
    }
}
