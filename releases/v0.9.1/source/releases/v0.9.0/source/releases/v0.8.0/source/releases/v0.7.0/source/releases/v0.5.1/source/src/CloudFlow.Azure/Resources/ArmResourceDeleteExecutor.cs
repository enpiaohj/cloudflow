using Azure;
using Azure.Core;
using Azure.ResourceManager;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Resources;

/// <summary>
/// 真实 Azure 的单资源删除执行器：走 ARM 的通用"按 ID 删除"接口
/// （<see cref="Azure.ResourceManager.Resources.GenericResource"/>），不需要为每种资源类型
/// 各自引入专门的 SDK 包——"资源"页要能删的类型不固定（网卡/磁盘/虚拟网络/公网 IP/NSG……），
/// 这正是 ARM 通用资源接口存在的意义。
/// </summary>
/// <remarks>
/// 目标资源完全由 <c>request.ResourceId</c> 解析（完整 Azure Resource ID，
/// 来自 <see cref="Modules.Network.Models.ResourceSummary.Id"/>）。
/// </remarks>
public sealed class ArmResourceDeleteExecutor(
    IAzureClientFactory clientFactory,
    ILogger<ArmResourceDeleteExecutor> logger) : IResourceDeleteExecutor
{
    public async Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var operation = await armClient.GetGenericResource(new ResourceIdentifier(request.ResourceId))
            .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM delete resource {ResourceId} (requestId {RequestId})",
            request.ResourceId, requestId);
        return requestId;
    }

    public async Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        try
        {
            await armClient.GetGenericResource(new ResourceIdentifier(request.ResourceId))
                .GetAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private async Task<ArmClient> CreateClientAsync(OperationRequest request, CancellationToken ct) =>
        await clientFactory.CreateAsync(RequestCredential.From(request), ct).ConfigureAwait(false);

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
