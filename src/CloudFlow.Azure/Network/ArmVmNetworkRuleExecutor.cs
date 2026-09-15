using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Azure.Network;

/// <summary>
/// 真实 NSG 规则的写操作执行器：OperationEngine → NetworkRuleHandlers → 本执行器
/// → IAzureClientFactory → ArmClient → NetworkSecurityGroupResource 的 SecurityRules。
///
/// 目标 NSG 由 Resource ID 定位（不猜、不编造），规则名取自规则 Resource ID 的末段。
/// </summary>
public sealed class ArmVmNetworkRuleExecutor(
    IAzureClientFactory clientFactory,
    ISubnetVmCounter subnetCounter,
    ILogger<ArmVmNetworkRuleExecutor> logger) : IVmNetworkRuleExecutor
{
    public async Task<string?> OpenPortAsync(
        OperationRequest request, string nsgId, NsgRuleDraft draft, CancellationToken ct = default)
    {
        var nsg = await GetNsgAsync(request, nsgId, ct).ConfigureAwait(false);

        // 同名规则的 CreateOrUpdate 是覆盖语义 —— 那会静默丢掉别人已有的规则，
        // 因此先确认不存在，再创建。
        if (await ExistsAsync(nsg, draft.Name, ct).ConfigureAwait(false))
        {
            throw new OperationValidationException(
                $"NSG 中已存在名为“{draft.Name}”的规则，请换一个名称。");
        }

        // 入站与出站是**同一种资源**，只差 Direction 与两个地址前缀各放哪一侧。
        // 两侧前缀都取自 draft，不在这里按方向推断 —— draft 已经把"谁是谁"写明了。
        var isOutbound = draft.Direction == NsgRuleDirection.Outbound;

        var data = new SecurityRuleData
        {
            Protocol = ToArmProtocol(draft.Protocol),
            Access = ToArmAccess(draft.Action),
            Direction = isOutbound ? SecurityRuleDirection.Outbound : SecurityRuleDirection.Inbound,
            Priority = draft.Priority,
            SourceAddressPrefix = draft.SourcePrefix,
            SourcePortRange = "*",
            DestinationAddressPrefix = draft.DestinationPrefix,
            DestinationPortRange = draft.Port.ToString(),
            Description = draft.Action == NsgRuleAction.Deny
                ? (isOutbound
                    ? $"CloudFlow 拒绝出站端口 {draft.Port}（目标 {draft.DestinationDisplay ?? draft.DestinationPrefix}）"
                    : $"CloudFlow 拒绝端口 {draft.Port}（来源 {draft.SourceDisplay}）")
                : (isOutbound
                    ? $"CloudFlow 开放出站端口 {draft.Port}（目标 {draft.DestinationDisplay ?? draft.DestinationPrefix}）"
                    : $"CloudFlow 打开端口 {draft.Port}（来源 {draft.SourceDisplay}）")
        };

        var operation = await nsg.GetSecurityRules()
            .CreateOrUpdateAsync(WaitUntil.Completed, draft.Name, data, ct)
            .ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM open port {Port} on NSG {Nsg} as rule {Rule} (requestId {RequestId})",
            draft.Port, nsgId, draft.Name, requestId);
        return requestId;
    }

    public async Task<string?> ChangePortAsync(
        OperationRequest request, string nsgId, string ruleName, int newPort, CancellationToken ct = default)
    {
        var nsg = await GetNsgAsync(request, nsgId, ct).ConfigureAwait(false);

        // §22：只改端口，其余字段沿用 Azure 上的当前值 —— 读-改-写而不是拿 UI 的缓存值覆盖。
        var existing = await nsg.GetSecurityRules()
            .GetAsync(ruleName, ct).ConfigureAwait(false);

        var data = existing.Value.Data;
        data.DestinationPortRange = newPort.ToString();

        var operation = await nsg.GetSecurityRules()
            .CreateOrUpdateAsync(WaitUntil.Completed, ruleName, data, ct)
            .ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM change port of rule {Rule} on NSG {Nsg} to {Port} (requestId {RequestId})",
            ruleName, nsgId, newPort, requestId);
        return requestId;
    }

    public async Task<string?> DeleteRuleAsync(
        OperationRequest request, string nsgId, string ruleName, CancellationToken ct = default)
    {
        var nsg = await GetNsgAsync(request, nsgId, ct).ConfigureAwait(false);

        var rule = await nsg.GetSecurityRules().GetAsync(ruleName, ct).ConfigureAwait(false);
        var operation = await rule.Value.DeleteAsync(WaitUntil.Completed, ct).ConfigureAwait(false);

        var requestId = RequestIdOf(operation.GetRawResponse());
        logger.LogInformation("ARM delete rule {Rule} on NSG {Nsg} (requestId {RequestId})",
            ruleName, nsgId, requestId);
        return requestId;
    }

    /// <summary>共享 NSG 影响面统计委托给子网计数实现（Resource Graph），失败返回 null 由调用方降级。</summary>
    public Task<int?> CountVmsInSubnetAsync(
        OperationRequest request, string subnetId, CancellationToken ct = default) =>
        subnetCounter.CountVmsInSubnetAsync(request, subnetId, ct);

    private async Task<NetworkSecurityGroupResource> GetNsgAsync(
        OperationRequest request, string nsgId, CancellationToken ct)
    {
        if (!ResourceIdentifier.TryParse(nsgId, out var resourceId) || resourceId is null)
        {
            throw new OperationValidationException($"无效的 NSG Resource ID：{nsgId}");
        }

        if (!string.Equals(resourceId.ResourceType.ToString(),
                "Microsoft.Network/networkSecurityGroups", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"Resource ID 不是网络安全组：{resourceId.ResourceType}");
        }

        var armClient = await clientFactory
            .CreateAsync(RequestCredential.From(request), ct)
            .ConfigureAwait(false);

        return armClient.GetNetworkSecurityGroupResource(resourceId);
    }

    private static async Task<bool> ExistsAsync(
        NetworkSecurityGroupResource nsg, string ruleName, CancellationToken ct)
    {
        try
        {
            await nsg.GetSecurityRules().GetAsync(ruleName, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="SecurityRuleProtocol"/> 是可扩展结构体，不能用 switch 的模式匹配，
    /// 只能逐个比较静态属性（本仓库在 EffectiveSecurityRuleProtocol 上踩过同样的坑）。
    /// </summary>
    private static SecurityRuleProtocol ToArmProtocol(NsgProtocol protocol) =>
        protocol == NsgProtocol.UDP
            ? SecurityRuleProtocol.Udp
            : protocol == NsgProtocol.Any
                ? SecurityRuleProtocol.Asterisk
                : SecurityRuleProtocol.Tcp;

    private static SecurityRuleAccess ToArmAccess(NsgRuleAction action) =>
        action == NsgRuleAction.Deny ? SecurityRuleAccess.Deny : SecurityRuleAccess.Allow;

    private static string? RequestIdOf(Response? response) =>
        response is not null && response.Headers.TryGetValue("x-ms-request-id", out var requestId)
            ? requestId
            : null;
}
