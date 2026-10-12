using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudFlow.App.Converters;
using CloudFlow.App.Themes;
using CloudFlow.App.ViewModels;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;

namespace CloudFlow.App.Views;

/// <summary>统一的只读操作详情，关闭时解除任务和 Scope 订阅。</summary>
public sealed class JobDetailsDialog : CfSidePanelWindow
{
    private readonly Guid _jobId;
    private readonly IJobStore? _store;
    private readonly IAuditLog? _audit;
    private readonly TextBlock _status = new();
    private readonly TextBox _result = ReadOnlyText();
    private readonly TextBox _context = ReadOnlyText();
    private readonly TextBox _history = ReadOnlyText();
    private readonly DispatcherTimer _auditTimer;
    private bool _closed;
    private OperationJob _job;
    private int _auditVersion;
    private bool _readingAudit, _auditRefreshPending;

    public JobDetailsDialog(OperationJob job, IJobStore? store = null, IAuditLog? audit = null, ScopeContext? scope = null)
    {
        _job = job; _jobId = job.JobId; _store = store; _audit = audit;
        Title = "操作详情"; Width = 460; Height = 720; FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0,0,0,16) };
        header.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(job.Display) ? job.OperationType : job.Display,
            FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Cf.TextMutedBrush");
        _status.Margin = new Thickness(0,8,0,0); header.Children.Add(_status); root.Children.Add(header);
        var body = new StackPanel();
        AddSection(body, "结果与进度", _result); AddSection(body, "任务与资源", _context);
        AddSection(body, "审计记录", _history);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll,1); root.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        var refresh = new Button { Content = "刷新详情", Margin = new Thickness(0,0,8,0) };
        refresh.SetResourceReference(StyleProperty,"Cf.SecondaryButton");
        refresh.Click += async (_,_) => { Update(_store?.Find(_jobId) ?? _job); await LoadAuditAsync(); };
        footer.Children.Add(refresh);
        var copy = new Button { Content = "复制详情", Margin = new Thickness(0,0,8,0) };
        copy.SetResourceReference(StyleProperty,"Cf.SecondaryButton");
        copy.Click += (_,_) => { try { Clipboard.SetText($"{Title} · {_status.Text}\n\n{_result.Text}\n\n{_context.Text}\n\n{_history.Text}"); } catch(System.Runtime.InteropServices.COMException) { copy.Content = "复制失败，请重试"; } };
        var close = new Button { Content = "关闭" }; close.SetResourceReference(StyleProperty,"Cf.PrimaryButton");
        close.Click += (_,_) => CompletePanel(false); footer.Children.Add(copy); footer.Children.Add(close);
        Grid.SetRow(footer,2); root.Children.Add(footer);
        var surface = new Border { Child = root, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        surface.SetResourceReference(Border.BackgroundProperty,"Cf.CardBrush");
        surface.SetResourceReference(Border.BorderBrushProperty,"Cf.BorderBrush"); Content = surface;
        _auditTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _auditTimer.Tick += async (_,_) => { _auditTimer.Stop(); await LoadAuditAsync(); };
        if (_store is not null) _store.JobChanged += JobChanged;
        if (scope is not null) scope.ScopeChanged += ScopeChanged;
        Closed += (_,_) => { _closed = true; _auditVersion++; _auditTimer.Stop(); if (_store is not null) _store.JobChanged -= JobChanged; if(scope is not null) scope.ScopeChanged -= ScopeChanged; };
        Update(_store?.Find(_jobId) ?? job); _ = LoadAuditAsync();
    }
    private static TextBox ReadOnlyText()
    {
        var box = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, MinHeight = 0, IsReadOnlyCaretVisible = true };
        box.SetResourceReference(Control.ForegroundProperty,"Cf.TextPrimaryBrush"); return box;
    }
    private static void AddSection(Panel body, string title, UIElement content)
    {
        var label = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,8,0,10) };
        body.Children.Add(label); body.Children.Add(content);
        body.Children.Add(new Border { Height=1, Margin=new Thickness(0,16,0,8), Background=(System.Windows.Media.Brush)Application.Current.FindResource("Cf.BorderBrush") });
    }
    private void Update(OperationJob job)
    {
        _job = job;
        _status.Text = $"{CfStatusTextConverter.Map(job.Status.ToString())} · 风险：{CfStatusTextConverter.Map(job.Risk.ToString())}";
        var result = new StringBuilder();
        result.AppendLine(JobPresentation.ResultText(job));
        if(!string.IsNullOrWhiteSpace(job.Summary)) result.AppendLine("结果摘要：" + job.Summary);
        if(!string.IsNullOrWhiteSpace(job.Error)) result.AppendLine("完整错误：\n" + job.Error);
        if(job.ImpactAffectedResources is not null) result.AppendLine("影响资源数：" + job.ImpactAffectedResources);
        _result.Text = result.Length == 0 ? "暂无结果，任务尚未完成。" : result.ToString();
        _context.Text = $"创建：{job.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n完成：{(job.CompletedAt is { } end ? end.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—")}\n操作类型：{job.OperationType}\n账户：{job.AccountDisplayName}\nAccount ID：{job.AccountId}\nTenant ID：{job.TenantId}\nSubscription ID：{job.SubscriptionId}\n资源 ID：{job.ResourceId}\nJob ID：{job.JobId}\nRequest ID：{job.RequestId ?? "—"}\nCorrelation ID：{job.CorrelationId}";
    }
    private void JobChanged(object? sender, OperationJob job)
    {
        if(job.JobId != _jobId) return;
        Dispatcher.BeginInvoke(new Action(() => { if(_closed) return; Update(job); _auditTimer.Stop(); _auditTimer.Start(); }));
    }
    private void ScopeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => { if(!_closed) Close(); }));
    private async Task LoadAuditAsync()
    {
        if (_closed) return;
        if (_readingAudit) { _auditRefreshPending = true; return; }
        var version = ++_auditVersion;
        if(_audit is null) { _history.Text = "暂无审计记录。"; return; }
        _readingAudit = true;
        try
        {
            var records = await Task.Run(() => _audit.QueryForJob(_jobId, _job.ResourceId).OrderBy(r=>r.Timestamp).ToArray());
            if(_closed || version != _auditVersion) return;
            _history.Text = records.Length == 0 ? "暂无审计记录，历史或演示任务可能未保存审计。" : string.Join("\n\n", records.Select(r=>$"{r.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {r.Outcome}\n{r.Detail}\n{r.Error}"));
        }
        catch(Exception ex) { if(!_closed && version==_auditVersion) _history.Text="审计读取失败：" + ex.Message; }
        finally
        {
            _readingAudit = false;
            if (_auditRefreshPending && !_closed) { _auditRefreshPending = false; _auditTimer.Start(); }
        }
    }
}
