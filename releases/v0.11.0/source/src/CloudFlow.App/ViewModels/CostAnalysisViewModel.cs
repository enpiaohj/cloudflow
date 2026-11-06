using CloudFlow.App.Infrastructure;
using CloudFlow.Azure.Cost;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.ViewModels;

/// <summary>Shell 的独立成本模块；跨页面保留日期和展示方式，查询生命周期由页面管理。</summary>
public sealed class CostAnalysisViewModel(ICostService service, ScopeContext context, IShellNavigation? navigation = null)
{
    internal ICostService Service { get; } = service;
    internal IShellNavigation? Navigation { get; } = navigation;
    internal ScopeContext Context { get; } = context;
    internal DateTime? From { get; set; }
    internal DateTime? To { get; set; }
    internal int Preset { get; set; }
    internal int Breakdown { get; set; }
}
