using CloudFlow.Azure.ResourceGraph;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using Microsoft.Extensions.Logging;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 混合 VM 清单：
/// - 未登录 → Mock 演示数据（概念图 42 台）
/// - 已登录 → Azure Resource Graph 真实清单；真实查询失败时**如实抛错**，
///   绝不回退 Mock 冒充用户订阅数据（全局规范：不隐藏 Error）。
/// 登录状态切换后由 ScopeChanged 触发页面刷新。
/// </summary>
public sealed class HybridVmInventoryService : IVmInventoryService
{
    private readonly MockVmInventoryService _mock;
    private readonly ResourceGraphVmInventoryService _real;
    private readonly IAccountSessionManager _sessions;
    private readonly ILogger<HybridVmInventoryService> _logger;

    public HybridVmInventoryService(
        MockVmInventoryService mock,
        ResourceGraphVmInventoryService real,
        IAccountSessionManager sessions,
        ILogger<HybridVmInventoryService> logger)
    {
        _mock = mock;
        _real = real;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VmSummary>> QueryAsync(ResourceScope scope, CancellationToken ct = default)
    {
        if (_sessions.IsConfigured)
        {
            var session = await _sessions.GetActiveSessionAsync(ct).ConfigureAwait(false);
            if (session is not null)
            {
                return await _real.QueryAsync(scope, ct).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("未登录 Azure，使用 Mock 演示数据");
        return await _mock.QueryAsync(scope, ct).ConfigureAwait(false);
    }
}
