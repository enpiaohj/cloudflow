using Azure;
using Azure.Core;
using Azure.ResourceManager;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
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

        try
        {
            var operation = await armClient.GetGenericResource(new ResourceIdentifier(request.ResourceId))
                .DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);

            var requestId = RequestIdOf(operation.GetRawResponse());
            logger.LogInformation("ARM delete resource {ResourceId} (requestId {RequestId})",
                request.ResourceId, requestId);
            return requestId;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 已经不在了 = 目标已达成（Handler 的 ExecuteAsync 已经在此之前查过一次存在性，
            // 这里兜住"查完之后、真正调用 Delete 之前它被别处删掉"的竞态）。
            logger.LogInformation("资源 {ResourceId} 在删除时已不存在，视为已达成", request.ResourceId);
            return null;
        }
        catch (RequestFailedException ex)
        {
            // 不能让 SDK 的完整诊断转储（Status / ErrorCode / 原始 Content / 全部 HTTP Header）
            // 原样冒给用户——摘成一句人能读的话。Azure 自己的错误信息通常会点名是被谁引用/占用
            // 导致删不掉（如"仍被 xxx 引用"），摘出来的这句话正好回答"资源引用情况"。
            throw new CloudFlowException(CloudFlowErrorCode.AzureError, AzureErrorMessages.Summarize(ex), ex);
        }
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
        catch (RequestFailedException ex)
        {
            // 这个方法现在是 DeleteResourceHandler.ExecuteAsync 执行前的存在性预检查，也是
            // VerifyAsync 用的同一个方法——真实踩过的坑：通用资源接口解析某些类型/区域组合的
            // API 版本时会撞上 NoRegisteredProviderFound（400），这里原来只兜住 404，
            // 其它 RequestFailedException 会带着完整诊断转储原样冒出去，正好绕过了 DeleteAsync
            // 那边已经摘干净的错误信息。
            throw new CloudFlowException(CloudFlowErrorCode.AzureError, AzureErrorMessages.Summarize(ex), ex);
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
