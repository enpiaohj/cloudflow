using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Resources;

/// <summary>
/// 真实 Azure 的资源组删除执行器：
/// OperationEngine → DeleteResourceGroupHandler → 本执行器 → IAzureClientFactory → ArmClient → ARM LRO。
/// </summary>
/// <remarks>
/// 目标资源完全由 <c>request.ResourceId</c> 解析（形如 <c>/subscriptions/{sub}/resourceGroups/{rg}</c>，
/// 没有 <c>providers/...</c> 后缀——这是资源组本身的 Resource ID 形态，Azure Resource ID 是唯一主键）。
///
/// <see cref="GetContainedResourcesAsync"/> 走 Resource Graph（与
/// <see cref="ArmRegionCatalog"/>/<see cref="ArmResourceGroupCatalog"/> 同一套原始 HttpClient 直调
/// 写法），因为组内资源类型/数量不固定，SDK 没有"查一个资源组下全部资源"的单一强类型接口，
/// 而这正是 Resource Graph 存在的意义（仓库既有纪律："Inventory/发现用 Azure Resource Graph"）。
/// </remarks>
public sealed class ArmResourceGroupDeleteExecutor(
    IAzureClientFactory clientFactory,
    ArmAccessTokenProvider tokenProvider,
    ILogger<ArmResourceGroupDeleteExecutor> logger) : IResourceGroupDeleteExecutor
{
    private const string ArgEndpoint =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<ResourceSummary>> GetContainedResourcesAsync(
        OperationRequest request, CancellationToken ct = default)
    {
        var rgName = ParseResourceGroupName(request.ResourceId);
        if (rgName is null)
        {
            return [];
        }

        try
        {
            var token = await tokenProvider.GetAsync(request.SubscriptionId, ct).ConfigureAwait(false);
            var query = $"Resources | where resourceGroup =~ '{rgName}' | project name, type, id, location";
            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["subscriptions"] = new[] { request.SubscriptionId }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(httpRequest, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "查询资源组 {ResourceGroup} 内资源失败：HTTP {Status}，Impact 分析将显示为空",
                    rgName, (int)response.StatusCode);
                return [];
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ResourceSummary>();
            foreach (var row in data.EnumerateArray())
            {
                if (row.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String &&
                    row.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String &&
                    row.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String &&
                    nameProp.GetString() is { Length: > 0 } name && typeProp.GetString() is { Length: > 0 } type &&
                    idProp.GetString() is { Length: > 0 } id)
                {
                    // location 可缺省（个别资源类型不带），缺省时由调用方退回资源组区域
                    var location = row.TryGetProperty("location", out var locationProp) &&
                                   locationProp.ValueKind == JsonValueKind.String
                        ? locationProp.GetString() ?? ""
                        : "";
                    result.Add(new ResourceSummary(name, type, id, location));
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询资源组 {ResourceGroup} 内资源异常，Impact 分析将显示为空", rgName);
            return [];
        }
    }

    public async Task<string?> DeleteAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var rgId = ResourceGroupResource.CreateResourceIdentifier(request.SubscriptionId, RequireRgName(request));

        try
        {
            var operation = await armClient.GetResourceGroupResource(rgId)
                .DeleteAsync(WaitUntil.Completed, cancellationToken: ct).ConfigureAwait(false);

            var requestId = RequestIdOf(operation.GetRawResponse());
            logger.LogInformation("ARM delete resource group {ResourceId} (requestId {RequestId})",
                request.ResourceId, requestId);
            return requestId;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 已经不在了 = 目标已达成（Handler 的 ExecuteAsync 已经在此之前查过一次存在性，
            // 这里兜住"查完之后、真正调用 Delete 之前它被别处删掉"的竞态）。
            logger.LogInformation("资源组 {ResourceId} 在删除时已不存在，视为已达成", request.ResourceId);
            return null;
        }
        catch (RequestFailedException ex)
        {
            // 不能让 SDK 的完整诊断转储（Status / ErrorCode / 原始 Content / 全部 HTTP Header）
            // 原样冒给用户——摘成一句人能读的话（真实报过的 Bug：用户看到的是一整段 HTTP 抓包）。
            throw new CloudFlowException(CloudFlowErrorCode.AzureError, AzureErrorMessages.Summarize(ex), ex);
        }
    }

    public async Task<bool> ExistsAsync(OperationRequest request, CancellationToken ct = default)
    {
        var armClient = await CreateClientAsync(request, ct).ConfigureAwait(false);
        var subscription = armClient.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(request.SubscriptionId));

        try
        {
            return (await subscription.GetResourceGroups()
                .ExistsAsync(RequireRgName(request), ct).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException ex)
        {
            // 这个方法是 DeleteResourceGroupHandler.ExecuteAsync 执行前的存在性预检查，也是
            // VerifyAsync 用的同一个方法——不能让非预期的 RequestFailedException 带着完整
            // 诊断转储原样冒出去（同 ArmResourceDeleteExecutor.ExistsAsync 的同一处修复）。
            throw new CloudFlowException(CloudFlowErrorCode.AzureError, AzureErrorMessages.Summarize(ex), ex);
        }
    }

    private async Task<ArmClient> CreateClientAsync(OperationRequest request, CancellationToken ct) =>
        await clientFactory.CreateAsync(RequestCredential.From(request), ct).ConfigureAwait(false);

    private static string RequireRgName(OperationRequest request) =>
        ParseResourceGroupName(request.ResourceId)
        ?? throw new CloudFlow.Core.Errors.OperationValidationException(
            $"无法从目标解析资源组：{request.ResourceId}。");

    /// <summary>从形如 …/resourceGroups/{rg}[/…] 的 Resource ID 里解析资源组名。</summary>
    private static string? ParseResourceGroupName(string resourceId) =>
        ResourceIdentifier.TryParse(resourceId, out var id) && id is not null
            ? id.ResourceGroupName
            : null;

    private static string? RequestIdOf(Response? response) =>
        response is not null &&
        response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
