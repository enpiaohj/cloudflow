using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.Infrastructure;
using CloudFlow.App.ViewModels;
using CloudFlow.Azure.Cost;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.Views;

/// <summary>当前 Scope 的只读成本查询；日期按 UTC 日历日计算，明细筛选在本地进行。</summary>
public sealed partial class CostAnalysisPage : UserControl
{
    private sealed record FilterOption(string Label, string? Value, string? Detail = null)
    { public string Tooltip => Detail ?? Label; }
    private ICostService _service = null!;
    private ScopeContext _context = null!;
    private ResourceScope _scope = null!;
    private CostAnalysisViewModel? _model;
    private bool _active;
    private int _contextVersion;
    private readonly LatestRead _read = new();
    private IReadOnlyList<ResourceCost> _rows = [];

    private bool _settingPreset;
    private bool _updatingFilters;
    private bool _loading;
    private bool _hasReport;

    private sealed record CostTableRow(string ResourceName, string ResourceType, string ResourceGroup, decimal Amount, string Currency, string SubscriptionId, string ResourceId);

    public CostAnalysisPage()
    {
        InitializeComponent();
        _preset.ItemsSource = new[] { "本月", "上月", "近 7 天", "近 30 天", "自定义" };
        _breakdown.ItemsSource = new[] { "按资源", "按资源组" };
        _breakdown.SelectionChanged += (_, _) => FilterRows();
        _preset.SelectionChanged += (_, _) => SetPreset();
        _from.SelectedDateChanged += DateEdited; _to.SelectedDateChanged += DateEdited;
        _search.TextChanged += (_, _) => FilterRows();
        foreach (var filter in new[] { _groups, _resources, _subscriptions }) filter.SelectionChanged += (_, _) => FilterRows();
        _preset.SelectedIndex = 0;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    public CostAnalysisPage(ICostService service, ScopeContext context) : this() => DataContext = new CostAnalysisViewModel(service, context);

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_active || DataContext is not CostAnalysisViewModel model) return;
        _model = model; _service = model.Service; _context = model.Context;
        _scope = _context.CurrentScope.Clone();
        if (model.From is not null && model.To is not null)
        {
            _settingPreset = true;
            try { _preset.SelectedIndex = model.Preset; _from.SelectedDate = model.From; _to.SelectedDate = model.To; _breakdown.SelectedIndex = model.Breakdown; }
            finally { _settingPreset = false; }
        }
        _active = true;
        _context.ScopeChanged += ScopeChanged; _context.PropertyChanged += ContextChanged;
        PopulateFilters();
        await QueryAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_active) return;
        _active = false; _contextVersion++; _read.Cancel(); SetLoading(false);
        _context.ScopeChanged -= ScopeChanged; _context.PropertyChanged -= ContextChanged;
        if (_model is not null)
        { _model.From = _from.SelectedDate; _model.To = _to.SelectedDate; _model.Preset = _preset.SelectedIndex; _model.Breakdown = _breakdown.SelectedIndex; }
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentHeight();
    private void Log_SizeChanged(object sender, RoutedEventArgs e) => UpdateContentHeight();
    private void UpdateContentHeight()
    {
        if (ContentLayout is null || CostViewport is null || LogExpander is null) return;
        // 给表格保留有效高度；小窗口由外层滚动，表格内部依然使用有限高度和行虚拟化。
        ContentLayout.Height = Math.Max(CostViewport.ActualHeight - 40, LogExpander.IsExpanded ? 900 : 680);
    }

    private void ContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ScopeContext.ActiveAccount)) ScopeChanged(sender, EventArgs.Empty); }
    private void ScopeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ScopeChanged(sender, e)); return; }
        if (!_active) return;
        _read.Cancel(); SetLoading(false); _rows = []; _hasReport = false;
        _total.Text = "—"; ReadTime.Text = ""; QueryState.Text = "范围已切换，准备查询…"; _status.Clear();
        PopulateFilters(); FilterRows();
        var version = ++_contextVersion;
        Dispatcher.BeginInvoke(async () => { if (_active && version == _contextVersion) await QueryAsync(); });
    }
    private void DateEdited(object? sender, SelectionChangedEventArgs e) { if (!_settingPreset) _preset.SelectedIndex = 4; }
    private void SetPreset()
    {
        if (_settingPreset || _preset.SelectedIndex < 0 || _preset.SelectedIndex == 4) return;
        _settingPreset = true;
        try
        {
            var today = DateTime.UtcNow.Date; var start = new DateTime(today.Year, today.Month, 1); var end = today;
            switch (_preset.SelectedIndex)
            {
                case 1: end = start.AddDays(-1); start = start.AddMonths(-1); break;
                case 2: start = today.AddDays(-6); break;
                case 3: start = today.AddDays(-29); break;
            }
            _from.SelectedDate = start; _to.SelectedDate = end;
        }
        finally { _settingPreset = false; }
    }

    private void PopulateFilters()
    {
        _updatingFilters = true;
        try
        {
            void Options(ComboBox box, string all, IEnumerable<FilterOption> options)
            {
                var selected = box.SelectedValue as string;
                var values = new[] { new FilterOption(all, null) }.Concat(options).ToArray();
                box.ItemsSource = values;
                box.SelectedItem = values.FirstOrDefault(v => v.Value == selected) ?? values[0];
            }
            Options(_groups, "全部资源组", _rows.Select(r => r.ResourceGroup).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x).Select(x => new FilterOption(x.Length == 0 ? "未关联资源组" : x, x)));
            // 使用完整 ResourceId 作为筛选值，同名资源不会混在一起。
            Options(_resources, "全部资源", _rows.GroupBy(r => r.ResourceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(r => r.ResourceName).Select(r => new FilterOption(r.ResourceId.Length == 0 ? r.ResourceName : $"{r.ResourceName} · {r.ResourceType} · {r.ResourceGroup} · {r.SubscriptionId}", r.ResourceId, r.ResourceId.Length > 0 ? r.ResourceId : r.ResourceName)));
            Options(_subscriptions, "全部订阅", _rows.Select(r => r.SubscriptionId).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x).Select(x => new FilterOption(_context.AvailableSubscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(x, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? x, x)));
        }
        finally { _updatingFilters = false; }
    }

    private void FilterRows()
    {
        if (_updatingFilters) return;
        var text = _search.Text.Trim();
        var group = _groups.SelectedValue as string; var resource = _resources.SelectedValue as string; var subscription = _subscriptions.SelectedValue as string;
        var filtered = _rows.Where(r => (group is null || r.ResourceGroup.Equals(group, StringComparison.OrdinalIgnoreCase))
            && (resource is null || r.ResourceId.Equals(resource, StringComparison.OrdinalIgnoreCase))
            && (subscription is null || r.SubscriptionId.Equals(subscription, StringComparison.OrdinalIgnoreCase))
            && (text.Length == 0 || $"{r.ResourceName} {r.ResourceId} {r.ResourceGroup} {r.SubscriptionId}".Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        var grouped = _breakdown.SelectedIndex == 1;
        BreakdownTitle.Text = grouped ? "资源组费用汇总" : "资源费用明细";
        CostTableRow[] display = grouped
            ? new CostReport(filtered, [], DateTimeOffset.UtcNow).GroupByResourceGroup().Select(r => new CostTableRow(r.ResourceGroup.Length == 0 ? "未关联资源组（其他费用）" : r.ResourceGroup,
                $"资源组汇总 · {r.ResourceCount} 个资源", r.ResourceGroup, r.Amount, r.Currency, r.SubscriptionId, "")).ToArray()
            : filtered.Select(r => new CostTableRow(r.ResourceName, r.ResourceType, r.ResourceGroup, r.Amount, r.Currency, r.SubscriptionId, r.ResourceId)).ToArray();
        _table.ItemsSource = display;
        ResultCount.Text = grouped ? $"{display.Length} 个资源组／币种汇总 · {filtered.Length} 条明细" : $"显示 {filtered.Length} / {_rows.Count} 条";
        FilteredTotal.Text = filtered.Length > 0 ? new CostReport(filtered, [], DateTimeOffset.UtcNow).TotalText : "—";
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _loading ? "正在查询费用…" : _rows.Count > 0 ? "没有匹配的费用明细" : _hasReport ? "暂无费用记录" : "尚无可显示的费用数据";
        EmptyHint.Text = _loading ? "查询期间可以取消，Azure 成本数据可能延迟更新。" : _rows.Count > 0 ? "调整资源、资源组或订阅筛选，或单击重置。" : "选择日期后查询；失败原因可在完整日志中查看。";
        UpdateSelection();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try { _groups.SelectedIndex = 0; _resources.SelectedIndex = 0; _subscriptions.SelectedIndex = 0; _search.Clear(); }
        finally { _updatingFilters = false; }
        FilterRows();
    }
    private void Table_LoadingRow(object sender, DataGridRowEventArgs e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
    private void Table_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        var row = _table.SelectedItem as CostTableRow;
        CopyResourceButton.IsEnabled = !string.IsNullOrWhiteSpace(row?.ResourceId);
        SelectedResource.Text = row is null ? "选择一行，可复制完整资源 ID" : row.ResourceId.Length == 0 ? row.ResourceName : row.ResourceId;
        SelectedResource.ToolTip = SelectedResource.Text;
    }
    private void CopyResource_Click(object sender, RoutedEventArgs e) { if (_table.SelectedItem is CostTableRow row && row.ResourceId.Length > 0) Copy(row.ResourceId); }
    private void CopyLog_Click(object sender, RoutedEventArgs e) => Copy(_status.Text);
    private void Copy(string text)
    { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { QueryState.Text = "剪贴板暂不可用，请重试。"; } }
    private void Home_Click(object sender, RoutedEventArgs e)
    { _model?.Navigation?.NavigateHome(); }
    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _read.Cancel(); SetLoading(false); QueryState.Text = "查询已取消，可重新查询";
        _status.Text += "\n查询已取消，未完成结果不会计入费用。"; FilterRows();
    }
    private void SetLoading(bool loading)
    { _loading = loading; QueryButton.IsEnabled = !loading; CancelButton.IsEnabled = loading; }

    private async Task QueryAsync()
    {
        if (!_active) return;
        using var read = _read.Begin();
        _scope = _context.CurrentScope.Clone();
        ScopeLabel.Text = string.IsNullOrWhiteSpace(_scope.ScopeName) ? _scope.Describe() : $"{_scope.ScopeName} · {_scope.Describe()}"; ScopeLabel.ToolTip = ScopeLabel.Text;
        _rows = []; _hasReport = false; _total.Text = "—"; ReadTime.Text = "";
        PopulateFilters(); SetLoading(false); FilterRows();
        if (_from.SelectedDate is not DateTime from || _to.SelectedDate is not DateTime to || from > to || to.Date > DateTime.UtcNow.Date)
        {
            QueryState.Text = "日期范围无效，请检查起止日期";
            _status.Text = "请选择有效日期范围，结束日期不能晚于今天（UTC）。"; LogExpander.IsExpanded = true; return;
        }
        PeriodLabel.Text = $"{from:yyyy-MM-dd} ～ {to:yyyy-MM-dd}（UTC）";
        QueryState.Text = "正在读取 Azure Cost Management…";
        _status.Text = $"查询范围：{_scope.Describe()}\n日期：{PeriodLabel.Text}\n成本类型：ActualCost\n正在查询…";
        SetLoading(true); FilterRows();
        var account = _context.ActiveAccount; var scope = _context.CurrentScope;
        try
        {
            var report = await _service.GetCostsAsync(_scope, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), true, read.Token);
            if (!read.IsCurrent || !_active || !ReferenceEquals(account, _context.ActiveAccount) || !ReferenceEquals(scope, _context.CurrentScope)) return;
            _rows = report.Rows; _hasReport = report.Failures.Count == 0 || report.Rows.Count > 0;
            _total.Text = report.Rows.Count == 0 && report.Failures.Count > 0 ? "—" : report.TotalText;
            var label = report.Failures.Count == 0 ? "查询完成 · 当前 Scope 合计" : report.Rows.Count > 0 ? $"部分结果 · {report.Failures.Count} 项失败，查看查询日志" : "查询未取得费用数据 · 查看查询日志";
            QueryState.Text = label;
            ReadTime.Text = $"读取时间 {report.RetrievedAt.LocalDateTime:HH:mm} · 数据可能延迟更新";
            _status.Text = $"查询范围：{_scope.Describe()}\n日期：{PeriodLabel.Text}\n成本类型：ActualCost\n结果：{label}\n明细数量：{_rows.Count}\n读取时间：{report.RetrievedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\nAzure 成本数据可能延迟更新。"
                + (report.Failures.Count > 0 ? "\n\n失败订阅不计入 Scope 合计或筛选合计；仅展示完整查询成功的订阅。\n" + string.Join("\n\n", report.Failures) : "");
            PopulateFilters();
        }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested) { }
        catch (Exception ex)
        { if (read.IsCurrent && _active) { QueryState.Text = "查询失败 · 查看完整日志"; _status.Text += $"\n\n查询失败：{ex.Message}"; } }
        finally { if (read.IsCurrent && _active) { SetLoading(false); FilterRows(); } }
    }
}
