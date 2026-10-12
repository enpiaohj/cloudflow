using CloudFlow.Azure.Network;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 混合 VM 网络服务：
/// - 未登录 → Mock 演示数据（概念图 2 的 WEB01 网络上下文）
/// - 已登录 → ARM 真实网络上下文（NIC / 专用 IP / 公网 IP / 子网 / NSG / Inbound Rules）
///
/// 真实读取失败时如实抛错，绝不回退 Mock 冒充用户订阅数据（全局规范：不隐藏 Error）。
/// </summary>
public sealed class HybridVmNetworkService : IVmNetworkService
{
    private readonly MockVmNetworkService _mock;
    private readonly ArmVmNetworkService _real;
    private readonly ScopeContext _scopeContext;
    private readonly ILogger<HybridVmNetworkService> _logger;

    public HybridVmNetworkService(
        MockVmNetworkService mock,
        ArmVmNetworkService real,
        ScopeContext scopeContext,
        ILogger<HybridVmNetworkService> logger)
    {
        _mock = mock;
        _real = real;
        _scopeContext = scopeContext;
        _logger = logger;
    }

    public Task<VmNetworkContext?> GetForVmAsync(string vmResourceId, CancellationToken ct = default)
    {
        if (_scopeContext.ActiveAccount is null)
        {
            _logger.LogDebug("未登录 Azure，使用 Mock 网络数据");
            return _mock.GetForVmAsync(vmResourceId, ct);
        }

        return _real.GetForVmAsync(vmResourceId, ct);
    }

    public Task<VmOutboundOverview?> GetOutboundAsync(string vmResourceId, CancellationToken ct = default)
    {
        if (_scopeContext.ActiveAccount is null)
        {
            return _mock.GetOutboundAsync(vmResourceId, ct);
        }

        return _real.GetOutboundAsync(vmResourceId, ct);
    }

    public Task<NsgSecurityRule?> FindRuleAsync(string vmResourceId, string ruleId, CancellationToken ct = default)
    {
        if (_scopeContext.ActiveAccount is null)
        {
            return _mock.FindRuleAsync(vmResourceId, ruleId, ct);
        }

        return _real.FindRuleAsync(vmResourceId, ruleId, ct);
    }

    public Task<IReadOnlyList<NsgSecurityRule>> GetInboundRulesAsync(
        string vmResourceId,
        CancellationToken ct = default)
    {
        if (_scopeContext.ActiveAccount is null)
        {
            return _mock.GetInboundRulesAsync(vmResourceId, ct);
        }

        return _real.GetInboundRulesAsync(vmResourceId, ct);
    }

    public Task<IReadOnlyList<NsgSecurityRule>> GetOutboundRulesAsync(
        string vmResourceId,
        CancellationToken ct = default)
    {
        if (_scopeContext.ActiveAccount is null)
        {
            return _mock.GetOutboundRulesAsync(vmResourceId, ct);
        }

        return _real.GetOutboundRulesAsync(vmResourceId, ct);
    }
}
