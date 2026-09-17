using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CloudFlow.App.Converters;
using CloudFlow.App.Infrastructure;
using CloudFlow.Data.Stores;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Terminal.Security;
using CloudFlow.Terminal.Ssh;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CloudFlow.App.ViewModels;

/// <summary>
/// 虚拟机列表页（概念图 1 右窗口）：
/// 搜索 / 筛选 / 分页（10 条/页）/ 行操作（重启 / 关机 / 解除分配）。
/// </summary>
public partial class VirtualMachinesViewModel : ObservableObject
{
    /// <summary>中文筛选选项 → 状态键（颜色转换器仍用英文键）。</summary>
    private static readonly Dictionary<string, string> StatusFilterMap = new()
    {
        ["运行中"] = "Running",
        ["已停止"] = "Stopped",
        ["已解除分配"] = "Deallocated",
        ["警告"] = "Warning"
    };

    private readonly IVmInventoryService _inventory;
    private readonly IVmPowerService _power;
    private readonly IVmProvisioningService _provisioning;
    private readonly IOperationEngine _engine;
    private readonly ScopeContext _scopeContext;
    private readonly IRegionCatalog _regionCatalog;
    private readonly IVmSizeCatalog _sizeCatalog;
    private readonly IVmPriceCatalog _priceCatalog;
    private readonly IResourceGroupCatalog _resourceGroupCatalog;
    private readonly IVmImageCatalog _imageCatalog;
    private readonly IJobStore _jobStore;
    private readonly IShellNavigation _navigation;
    private readonly AppSettingsStore _settings;

    /// <summary>连接流程：与 VM 详情页共用同一份"从凭据到会话"的实现。</summary>
    private readonly SshConnectFlow _connectFlow;

    /// <summary>批量连接用的终端面板适配器。</summary>
    private readonly TerminalPanelSessionHost _sessionHost;

    private readonly ILogger _logger;

    /// <summary>批量连接进行中的取消源；为空表示没有正在进行的批量连接。</summary>
    private CancellationTokenSource? _connectCts;

    private List<VmSummary> _allVms = [];
    private List<VmSummary> _filtered = [];

    /// <summary>
    /// 已订阅 <c>IsChecked</c> 变更的 VM 全集。
    /// 每次重建清单都要先退订旧的 —— <c>VmSummary</c> 是长生命周期对象，
    /// 只订不退会让每刷新一次就多留一批悬挂订阅。
    /// </summary>
    private IReadOnlyList<VmSummary> _trackedVms = [];

    /// <summary>每页条数（概念图分页下拉；可选值由设置里的「默认每页条数」约束）。</summary>
    private int _pageSize;

    public IReadOnlyList<string> PageSizeOptions { get; } =
        [.. AppSettings.SupportedPageSizes.Select(size => $"{size} 条/页")];

    [ObservableProperty]
    private string _selectedPageSize = "";

    partial void OnSelectedPageSizeChanged(string value)
    {
        var number = value.Split(' ')[0];
        if (int.TryParse(number, out var size) && size > 0)
        {
            _pageSize = size;
        }
        CurrentPage = 1;
        ApplyFilters();
    }

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private string _statusFilter = "全部状态";

    [ObservableProperty]
    private string _resourceGroupFilter = "全部资源组";

    [ObservableProperty]
    private string _regionFilter = "全部区域";

    public IReadOnlyList<string> StatusOptions { get; } =
        ["全部状态", .. StatusFilterMap.Keys];

    public IReadOnlyList<string> ResourceGroupOptions { get; private set; } = ["全部资源组"];

    public IReadOnlyList<string> RegionOptions { get; private set; } = ["全部区域"];

    [ObservableProperty]
    private ObservableCollection<VmSummary> _pageItems = [];

    /// <summary>
    /// 已勾选的台数。<b>跨页计数</b> —— 勾选状态挂在 <see cref="VmSummary"/> 实例上，
    /// 翻页只是换切片，勾选不会丢。「连接选中 (N)」的显隐与文案都由它驱动。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChecked))]
    [NotifyPropertyChangedFor(nameof(ConnectActionText))]
    private int _checkedCount;

    public bool HasChecked => CheckedCount > 0;

    /// <summary>
    /// 标题行按钮的文案：勾 1 台就是「连接」，勾 2 台以上才是「批量连接」。
    /// </summary>
    /// <remarks>
    /// 用户定的口径：默认「连接」，选了 1 台以上才变成「批量连接」——
    /// 只勾一台时叫"批量"是名不副实的。
    /// </remarks>
    public string ConnectActionText =>
        CheckedCount > 1 ? $"批量连接 ({CheckedCount})" : "连接";

    /// <summary>
    /// 表头复选框的显示状态。<b>只看当前页</b> —— 用户已定：全选只作用于本页。
    /// </summary>
    public bool IsPageAllChecked => PageItems.Count > 0 && PageItems.All(vm => vm.IsChecked);

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _pageInfoText = "";

    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private string? _infoSeverity;

    [ObservableProperty]
    private bool _isLoading;

    public VirtualMachinesViewModel(
        IVmInventoryService inventory,
        IVmPowerService power,
        IVmProvisioningService provisioning,
        IOperationEngine engine,
        ScopeContext scopeContext,
        IRegionCatalog regionCatalog,
        IVmSizeCatalog sizeCatalog,
        IVmPriceCatalog priceCatalog,
        IResourceGroupCatalog resourceGroupCatalog,
        IVmImageCatalog imageCatalog,
        IShellNavigation navigation,
        IJobStore jobStore,
        AppSettingsStore settings,
        SshConnectFlow connectFlow,
        TerminalPanelSessionHost sessionHost,
        ILogger<VirtualMachinesViewModel>? logger = null)
    {
        _inventory = inventory;
        _power = power;
        _provisioning = provisioning;
        _engine = engine;
        _scopeContext = scopeContext;
        _regionCatalog = regionCatalog;
        _sizeCatalog = sizeCatalog;
        _priceCatalog = priceCatalog;
        _resourceGroupCatalog = resourceGroupCatalog;
        _imageCatalog = imageCatalog;
        _navigation = navigation;
        _settings = settings;
        _connectFlow = connectFlow;
        _sessionHost = sessionHost;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _scopeContext.ScopeChanged += OnScopeChanged;
        _jobStore = jobStore;
        _jobStore.JobChanged += OnJobChanged;
        _settings.Changed += OnSettingsChanged;

        // 设置里的「默认每页条数」是**初值**：列表页内改只影响本次会话，
        // 改设置则当场生效（见 OnSettingsChanged）
        ApplyPageSizeFromSettings();
    }

    /// <summary>
    /// 自动刷新计时器。只在虚拟机列表页**可见**时走 —— 停在别的页面还按点打 Azure，
    /// 是在为一个没人在看的表格持续花钱和额度。
    /// </summary>
    private DispatcherTimer? _autoRefreshTimer;
    private bool _isPageActive;

    /// <summary>由 Shell 在切页时告知本页是否可见。</summary>
    public void SetPageActive(bool active)
    {
        _isPageActive = active;
        SyncAutoRefreshTimer();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // 设置可能来自非 UI 线程的落盘回调，计时器必须在 UI 线程上动
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyPageSizeFromSettings();
            SyncAutoRefreshTimer();
        });
    }

    private void ApplyPageSizeFromSettings()
    {
        var size = _settings.Current.DefaultPageSize;
        _pageSize = size;
        SelectedPageSize = $"{size} 条/页";
    }

    /// <summary>按当前设置与可见性启停计时器。关闭（0 秒）或页面不可见时一律停表。</summary>
    private void SyncAutoRefreshTimer()
    {
        var seconds = _settings.Current.AutoRefreshSeconds;

        if (!_isPageActive || seconds <= 0)
        {
            _autoRefreshTimer?.Stop();
            return;
        }

        _autoRefreshTimer ??= CreateTimer();
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(seconds);
        // 重新赋值 Interval 后必须重开，否则改小间隔不会立即生效
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += async (_, _) =>
        {
            // 自动刷新沿用既有的 RefreshAsync：筛选条件与页码是它内部状态，
            // 不会因为刷新被重置（ApplyFilters 只在总页数变小时夹紧页码）
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                // Timer.Tick 里逃出去的异常会成为未处理异常直接结束进程 ——
                // 后台刷新失败只该报出来并停表，不该把应用带走。
                // 停表而不是继续重试：真的连不上时，每 30 秒弹一次同样的错更糟。
                _autoRefreshTimer?.Stop();
                InfoSeverity = "Failed";
                InfoText = $"自动刷新失败，已暂停：{ex.Message}";
            }
        };
        return timer;
    }

    /// <summary>Shell 全局搜索跳转入口。</summary>
    public void SetExternalFilter(string filter)
    {
        FilterText = filter;
        CurrentPage = 1;
        ApplyFilters();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            // 自动刷新会整份重建清单（新实例），而勾选挂在 VmSummary 实例上 ——
            // 不搬过刷新边界的话，用户勾到一半名单会被静默清空（后台每隔一段时间就刷一次）。
            var previouslyChecked = _allVms
                .Where(vm => vm.IsChecked)
                .Select(vm => vm.ResourceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _allVms = [.. await _inventory.QueryAsync(_scopeContext.CurrentScope)];

            foreach (var vm in _allVms.Where(vm => previouslyChecked.Contains(vm.ResourceId)))
            {
                // 在挂订阅之前置位，避免为每一条都触发一次计数重算
                vm.IsChecked = true;
            }

            ResourceGroupOptions = ["全部资源组", .. _allVms.Select(vm => vm.ResourceGroupName).Distinct().OrderBy(x => x)];
            RegionOptions = ["全部区域", .. _allVms.Select(vm => vm.Region).Distinct().OrderBy(x => x)];
            OnPropertyChanged(nameof(ResourceGroupOptions));
            OnPropertyChanged(nameof(RegionOptions));
            TrackCheckedState(_allVms);
            ApplyFilters();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnStatusFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnResourceGroupFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    partial void OnRegionFilterChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        IEnumerable<VmSummary> query = _allVms;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText.Trim();
            query = query.Where(vm =>
                vm.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                vm.ResourceGroupName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (vm.PublicIp?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                vm.VmSize.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (StatusFilter is not "全部状态" and not null and not "")
        {
            query = StatusFilterMap.TryGetValue(StatusFilter, out var key)
                ? (key == "Warning"
                    ? query.Where(vm => vm.HasWarning)
                    : query.Where(vm => vm.PowerState.ToString() == key))
                : query;
        }

        if (ResourceGroupFilter.StartsWith("全部") is false)
        {
            query = query.Where(vm => vm.ResourceGroupName == ResourceGroupFilter);
        }

        if (RegionFilter.StartsWith("全部") is false)
        {
            query = query.Where(vm => vm.Region == RegionFilter);
        }

        _filtered = [.. query.OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)];

        TotalPages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }

        var skip = (CurrentPage - 1) * _pageSize;
        PageItems = [.. _filtered.Skip(skip).Take(_pageSize)];

        var from = _filtered.Count == 0 ? 0 : skip + 1;
        var to = Math.Min(skip + _pageSize, _filtered.Count);
        PageInfoText = $"显示 {from}–{to}，共 {_filtered.Count} 台虚拟机";
        OnPropertyChanged(nameof(PageNumbers));

        // 表头复选框看的是"当前页"，换页 / 换筛选后它的显示状态必须重算
        OnPropertyChanged(nameof(IsPageAllChecked));
    }

    // ── 勾选与连接 ────────────────────────────────────────────────

    /// <summary>重建清单后重新挂订阅。旧清单必须先退订，否则每刷新一次就多留一批悬挂订阅。</summary>
    private void TrackCheckedState(IReadOnlyList<VmSummary> vms)
    {
        foreach (var vm in _trackedVms)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _trackedVms = vms;

        foreach (var vm in _trackedVms)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateCheckedCount();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VmSummary.IsChecked))
        {
            UpdateCheckedCount();
        }
    }

    private void UpdateCheckedCount()
    {
        CheckedCount = _allVms.Count(vm => vm.IsChecked);

        // 逐行勾选同样会改变"本页是否全选"，表头那个框得跟着动
        OnPropertyChanged(nameof(IsPageAllChecked));
    }

    /// <summary>
    /// 表头复选框：<b>只作用于当前页</b>（用户已定，不做跨页全选）。
    /// 本页已全勾就全部取消，否则全部勾上 —— 这就是这套"三态"的全部语义。
    /// </summary>
    [RelayCommand]
    private void TogglePageChecked()
    {
        var target = !IsPageAllChecked;

        foreach (var vm in PageItems)
        {
            vm.IsChecked = target;
        }

        OnPropertyChanged(nameof(IsPageAllChecked));
    }

    /// <summary>
    /// 行菜单「连接」：单台。
    /// Windows 走 <c>mstsc</c>（与详情页一致 —— 单台是用户明确指名的那一台）；
    /// Linux 弹凭据对话框后交给 <see cref="SshConnectFlow"/>，走窗口底部的终端面板。
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync(VmSummary? vm)
    {
        if (vm is null)
        {
            return;
        }

        var target = vm.PublicIp ?? vm.PrivateIp;
        if (string.IsNullOrEmpty(target))
        {
            InfoSeverity = "Failed";
            InfoText = $"{vm.Name}：没有可用的 IP 地址，无法连接。";
            return;
        }

        try
        {
            if (vm.OsType == VmOsType.Windows)
            {
                System.Diagnostics.Process.Start("mstsc", $"/v:{target}");
                return;
            }

            var dialog = new Views.SshCredentialDialog(_connectFlow.CredentialLibrary, target, vm.ResourceId)
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() is not true || dialog.Result is null)
            {
                return;
            }

            var input = dialog.Result;
            var request = new SshConnectRequest
            {
                CredentialId = input.UseCredentialId,
                TransientInput = input,
            };

            var result = await _connectFlow.ConnectAsync(
                vm, target, request, _connectFlow.CreateInteractivePolicy());

            InfoSeverity = result.Connected ? "Succeeded" : "Failed";
            InfoText = result.Connected
                ? $"已连接 {vm.Name}（{target}），会话在窗口底部的终端面板里。"
                : result.FailureMessage;
        }
        catch (Exception ex)
        {
            InfoSeverity = "Failed";
            InfoText = $"启动连接失败：{ex.Message}";
        }
    }

    // ── 批量连接 ──────────────────────────────────────────────────

    /// <summary>批量连接进行中 —— 进度写进 InfoBar，取消入口由它显隐。</summary>
    [ObservableProperty]
    private bool _isConnecting;

    [RelayCommand]
    private void CancelConnect() => _connectCts?.Cancel();

    /// <summary>
    /// 标题行「连接选中 (N)」：批量连接所有勾选的虚拟机。
    /// </summary>
    /// <remarks>
    /// <para>用户已定的口径：<b>只连 Linux</b>（Windows 跳过并如实告知）、<b>完全串行</b>、
    /// 首次遇到的主机指纹<b>合并成一个确认框</b>、指纹变化<b>不批量确认</b>。</para>
    /// <para><b>每台各用自己记住的那条凭据</b>（2026-09-13 定）：所以这里不是"一套凭据套 N 台"，
    /// 而是逐台查它记住的凭据；只有没记住的那些才用对话框里选的那条补上。
    /// 全都记住过就直接连、不弹框 —— 那正是"选了就一直用，除非改连接配置"的兑现。</para>
    /// </remarks>
    [RelayCommand]
    private async Task ConnectSelectedAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        var selected = _allVms.Where(vm => vm.IsChecked).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var targets = selected
            .Select(vm => new SshBatchTarget(
                vm.ResourceId, vm.Name, vm.PublicIp ?? vm.PrivateIp ?? "",
                vm.OsType == VmOsType.Linux))
            .ToList();

        using var cts = new CancellationTokenSource();
        _connectCts = cts;
        IsConnecting = true;
        InfoSeverity = "Validating";

        try
        {
            // ① 逐台取它记住的凭据
            var remembered = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets.Where(t => t.IsConnectable))
            {
                if (await _connectFlow.CredentialLibrary
                        .GetDefaultCredentialIdForVmAsync(target.VmResourceId, cts.Token) is { } id)
                {
                    remembered[target.VmResourceId] = id;
                }
            }

            // ② 有没记住的 → 弹一次对话框，选择**只补给没记住的那些**；全都记住过就直接连、不打扰
            var missing = targets
                .Where(t => t.IsConnectable && !remembered.ContainsKey(t.VmResourceId))
                .ToList();

            SshConnectRequest? fallback = null;
            if (missing.Count > 0)
            {
                var dialog = new Views.SshCredentialDialog(
                    _connectFlow.CredentialLibrary,
                    missing[0].Host,
                    vmResourceId: null,   // 批量没有单一目标，让对话框退回"全局最近使用"
                    targetCaption: $"批量连接 · 共 {targets.Count} 台，其中 {missing.Count} 台尚未记住凭据")
                {
                    Owner = Application.Current?.MainWindow
                };

                if (dialog.ShowDialog() is not true || dialog.Result is null)
                {
                    InfoSeverity = null;
                    InfoText = null;
                    return;
                }

                fallback = new SshConnectRequest
                {
                    CredentialId = dialog.Result.UseCredentialId,
                    TransientInput = dialog.Result,
                };
            }

            // ③ 每条用到的凭据各解析一次（多台共用同一条时只解密一次）。
            //    ⚠️ 解不开的**逐台标失败，绝不改用别的凭据** ——
            //    拿另一条凭据去连另一台机器，是这类功能里最难排查的一类错误。
            var resolved = new Dictionary<Guid, ResolvedSshCredential>();
            var unresolvable = new HashSet<Guid>();

            try
            {
                var needed = remembered.Values
                    .Concat(fallback?.CredentialId is Guid fallbackId ? [fallbackId] : Array.Empty<Guid>())
                    .Distinct()
                    .ToList();

                foreach (var id in needed)
                {
                    if (await _connectFlow.CredentialLibrary.ResolveAsync(id, cts.Token) is { } credential)
                    {
                        resolved[id] = credential;
                    }
                    else
                    {
                        unresolvable.Add(id);
                    }
                }

                // 临时输入兜底：私钥正文只读一次，这一批共用同一份
                string? transientKeyBody = null;
                if (fallback is { CredentialId: null, TransientInput: { } transient } &&
                    transient.AuthType == SshCredentialAuthType.PrivateKeyFile &&
                    transient.PrivateKeyPath is { } keyPath)
                {
                    transientKeyBody = await File.ReadAllTextAsync(keyPath, cts.Token);
                }

                // 解不开的直接计入失败，且**不交给编排层** —— 否则它会拿这条去建连
                var outcomes = new List<SshBatchItemResult>();
                var connectable = new List<SshBatchTarget>();

                foreach (var target in targets.Where(t => t.IsConnectable))
                {
                    var id = remembered.TryGetValue(target.VmResourceId, out var own)
                        ? own
                        : fallback?.CredentialId;

                    if (id is { } unresolvedId && unresolvable.Contains(unresolvedId))
                    {
                        outcomes.Add(new SshBatchItemResult(target, SshBatchItemState.Failed,
                            SshConnectionErrorCode.CredentialMissing,
                            "记住的凭据已无法解密（保险库文件可能被删除，或来自其他机器），请单独连接并重新选择"));
                        continue;
                    }

                    connectable.Add(target);
                }

                var progress = new Progress<SshBatchProgress>(p => InfoText = DescribeProgress(p));

                var report = await new SshBatchConnector(
                        _connectFlow.KnownHosts,
                        _sessionHost,
                        confirmNewHosts: (keys, _) =>
                        {
                            var confirm = new Views.BatchHostKeyConfirmDialog(keys)
                            {
                                Owner = Application.Current?.MainWindow
                            };
                            return Task.FromResult(confirm.ShowDialog() is true);
                        },
                        progress: progress,
                        logger: _logger)
                    .RunAsync(
                        connectable,
                        (target, policy) => BuildBatchOptions(
                            target, policy, remembered, fallback, resolved, transientKeyBody),
                        cts.Token);

                var combined = new SshBatchReport([.. report.Items, .. outcomes]);

                InfoSeverity = combined.HasFailure ? "Failed" : "Succeeded";
                InfoText = combined.Summary;
            }
            finally
            {
                foreach (var credential in resolved.Values)
                {
                    credential.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            InfoSeverity = null;
            InfoText = "已取消批量连接。";
        }
        catch (Exception ex)
        {
            InfoSeverity = "Failed";
            InfoText = $"批量连接失败：{ex.Message}";
        }
        finally
        {
            IsConnecting = false;
            _connectCts = null;
        }
    }

    /// <summary>逐台构建连接参数 —— 「每台各用自己记住的那条凭据」的落点。</summary>
    private static SshConnectionOptions BuildBatchOptions(
        SshBatchTarget target,
        SshHostKeyPolicy policy,
        IReadOnlyDictionary<string, Guid> remembered,
        SshConnectRequest? fallback,
        IReadOnlyDictionary<Guid, ResolvedSshCredential> resolved,
        string? transientKeyBody)
    {
        if (remembered.TryGetValue(target.VmResourceId, out var ownId) &&
            resolved.TryGetValue(ownId, out var own))
        {
            return FromResolved(target, own, policy);
        }

        if (fallback?.CredentialId is Guid sharedId && resolved.TryGetValue(sharedId, out var shared))
        {
            return FromResolved(target, shared, policy);
        }

        // 临时输入兜底（对话框里没选库中凭据）
        var transient = fallback?.TransientInput;
        var isPassword = transient?.AuthType != SshCredentialAuthType.PrivateKeyFile;

        return new SshConnectionOptions
        {
            Host = target.Host,
            Port = target.Port,
            Username = transient?.Username ?? "azureuser",
            AuthType = isPassword ? SshAuthType.Password : SshAuthType.PrivateKey,
            Password = isPassword ? transient?.Password : null,
            PrivateKey = isPassword ? null : transientKeyBody,
            Passphrase = isPassword ? null : transient?.Passphrase,
            HostKeyPolicy = policy
        };
    }

    private static SshConnectionOptions FromResolved(
        SshBatchTarget target, ResolvedSshCredential credential, SshHostKeyPolicy policy) => new()
        {
            Host = target.Host,
            Port = target.Port,
            Username = credential.Username,
            AuthType = credential.AuthType,
            Password = credential.Password,
            PrivateKey = credential.PrivateKey,
            Passphrase = credential.Passphrase,
            HostKeyPolicy = policy
        };

    private static string DescribeProgress(SshBatchProgress progress) => progress.Phase switch
    {
        SshBatchPhase.Probing =>
            $"正在探测主机指纹：{progress.TargetName}（{progress.Completed + 1}/{progress.Total}）…",
        SshBatchPhase.AwaitingHostKeyConfirmation => "等待确认新主机的指纹…",
        SshBatchPhase.Connecting =>
            $"正在连接：{progress.TargetName}（{progress.Completed + 1}/{progress.Total}）…",
        _ => "正在批量连接…"
    };

    /// <summary>分页按钮数字（最多显示 7 页）。</summary>
    public IReadOnlyList<int> PageNumbers =>
        [.. Enumerable.Range(1, TotalPages).Take(7)];

    [RelayCommand]
    private void GoToPage(int page)
    {
        if (page is >= 1 && page <= TotalPages)
        {
            CurrentPage = page;
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            GoToPage(CurrentPage - 1);
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            GoToPage(CurrentPage + 1);
        }
    }

    [RelayCommand]
    private void OpenDetail(VmSummary vm) => _navigation.NavigateToVmDetail(vm);

    /// <summary>
    /// 标题行「创建虚拟机」：密码先进入 DPAPI 凭据库，持久化 OperationRequest 只保存凭据 Id。
    /// </summary>
    [RelayCommand]
    private void CreateVm()
    {
        var subscriptions = _scopeContext.AvailableSubscriptions
            .Select(subscription => new Views.ProvisioningSubscriptionOption(
                subscription.SubscriptionId, subscription.DisplayName))
            .ToList();

        // Demo 模式没有 Azure SubscriptionProfile，退回从演示清单和当前 Scope 归纳的订阅。
        foreach (var subscriptionId in _allVms.Select(vm => vm.SubscriptionId)
                     .Concat(_scopeContext.CurrentScope.SubscriptionIds)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (subscriptions.All(item => !string.Equals(
                    item.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase)))
            {
                subscriptions.Add(new Views.ProvisioningSubscriptionOption(subscriptionId, subscriptionId));
            }
        }

        // 按资源组分组反推每个资源组"上次用的区域"，作为资源组下拉的推断清单——
        // 真实清单回来后 LoadLiveCatalogsAsync 会换掉它，但换之前这份至少比空着强。
        var fallbackResourceGroups = _allVms
            .Where(vm => !string.IsNullOrWhiteSpace(vm.ResourceGroupName))
            .GroupBy(vm => vm.ResourceGroupName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => IsPlatformManagedResourceGroup(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Views.ResourceGroupOption(group.Key, group.First().Region ?? ""))
            .ToList();

        var defaultSubscriptionId = subscriptions.Count > 0 ? subscriptions[0].SubscriptionId : null;

        // 区域/规格目录是真实网络请求，不能在这里 await——那会让点了按钮之后好几秒都看不到对话框。
        // 先用推断/演示清单垫上、立即弹出对话框，真实数据回来后再回调对话框换掉下拉内容（见下方
        // LoadLiveCatalogsAsync）；也因此不能 ConfigureAwait(false)，后面 new Window 必须留在 UI 线程。
        var fallbackRegions = _allVms.Select(vm => vm.Region)
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(region => region, StringComparer.OrdinalIgnoreCase)
            .Select(name => new Views.RegionOption(name, name))
            .ToList();

        // 演示清单常见规格的核数/内存是公开、固定的信息，写死展示用，不是真的假装查了目录。
        // 全都标成 Gen2——B/D 系列现代规格默认就是，向导会优先选中这里的第一项。
        List<Views.VmSizeOption> fallbackSizes =
        [
            new("Standard_B1s", 1, 1024, "V1,V2"),
            new("Standard_B2s", 2, 4096, "V1,V2"),
            new("Standard_B2ms", 2, 8192, "V1,V2"),
            new("Standard_D2s_v5", 2, 8192, "V2")
        ];

        var dialog = new Views.CreateVmWizardDialog(
            subscriptions, fallbackResourceGroups, fallbackRegions, fallbackSizes, _priceCatalog, _imageCatalog)
        {
            Owner = Application.Current?.MainWindow
        };

        if (_scopeContext.ActiveAccount is not null && !string.IsNullOrWhiteSpace(defaultSubscriptionId))
        {
            _ = LoadLiveCatalogsAsync(dialog, defaultSubscriptionId);
        }

        if (dialog.ShowDialog() is not true || dialog.Result is not { } result)
        {
            return;
        }

        // 对话框已经关闭——真正的提交（可能要几十秒到几分钟）转到后台跑，不再堵住 UI。
        // 立即给一句"已提交"反馈，后续子步骤进度会持续更新到这同一条横幅上（顶栏徽标同步显示）。
        InfoSeverity = "Validating";
        InfoText = $"创建虚拟机 {result.Parameters.GetValueOrDefault(CreateVmHandler.PayloadVmName)} 已提交…";
        _ = SubmitCreateVmAsync(
            result,
            note => InfoText = $"正在创建虚拟机 {result.Parameters.GetValueOrDefault(CreateVmHandler.PayloadVmName)}：{note}",
            CancellationToken.None);
    }

    /// <summary>
    /// 由 <see cref="Views.CreateVmWizardDialog"/> 在用户点"创建"后回调，对话框会一直等这个方法
    /// 返回结果——之前是点了就立刻关窗口、提交在关闭之后才真正跑，Azure 报错（比如规格和镜像的
    /// Hypervisor Generation 不匹配）时窗口早就消失了，用户对不上"是刚才那次点击导致的"。
    /// 现在窗口留到提交真正有结果才关，失败原因也摆在原处，不用重填一遍两步表单。
    /// </summary>
    private async Task<Views.CreateVmSubmitOutcome> SubmitCreateVmAsync(
        Views.CreateVmResult result, Action<string> onProgress, CancellationToken ct)
    {
        // 创建虚拟机可能需要走"批准执行"那一步才真正 Execute（见 Approve/ApproveJobAsync），
        // 也可能因为审批档位关闭而在这次调用里直接执行完——两条路径都要把子步骤进度转发出去，
        // 所以在真正调用 _provisioning.CreateAsync 之前就订阅，按 OperationType 匹配
        // （此刻 Job 还没创建出来，拿不到具体 JobId）。对话框已经关闭、这次调用是后台跑的，
        // 按类型匹配在"同时有两台虚拟机在各自创建"时会互相串台——目前只按单个创建同时只有
        // 一个在跑处理，多开一个的场景留到真的有人这么用时再收紧成按 JobId 精确匹配。
        void OnProgressChanged(object? sender, OperationJob job)
        {
            if (job.OperationType != ComputeModule.OperationCreate || string.IsNullOrEmpty(job.ProgressNote))
            {
                return;
            }

            // JobChanged 不保证在 UI 线程上触发（JobStore 落盘是真实异步 I/O，链路上一旦有
            // ConfigureAwait(false) 就可能续在线程池线程上）——本仓库其它订阅方都用
            // Dispatcher.BeginInvoke 兜底，这里直接调用 UI 回调漏了这一步，会抛
            // "调用线程无法访问此对象"。
            Application.Current?.Dispatcher.BeginInvoke(() => onProgress(job.ProgressNote!));
        }

        _jobStore.JobChanged += OnProgressChanged;
        var parameters = new Dictionary<string, string>(result.Parameters, StringComparer.OrdinalIgnoreCase);
        try
        {
            SshCredential? passwordCredential = null;
            if (result.Password is { Length: > 0 } password)
            {
                if (await _connectFlow.CredentialLibrary
                        .IsNameTakenAsync(result.PasswordCredentialName).ConfigureAwait(true))
                {
                    return Views.CreateVmSubmitOutcome.Failed(
                        $"凭据名称「{result.PasswordCredentialName}」已存在。");
                }

                passwordCredential = await _connectFlow.CredentialLibrary.CreateAsync(
                    new SshCredential
                    {
                        Name = result.PasswordCredentialName!,
                        Username = parameters[CreateVmHandler.PayloadAdminUsername],
                        AuthType = SshAuthType.Password,
                        Description = $"创建虚拟机 {parameters[CreateVmHandler.PayloadVmName]} 时保存的管理员密码"
                    },
                    secret: password,
                    privateKeyBody: null).ConfigureAwait(true);
                parameters[CreateVmHandler.PayloadCredentialId] = passwordCredential.Id.ToString();
            }

            InfoText = $"正在提交：创建虚拟机 {parameters[CreateVmHandler.PayloadVmName]}…";
            InfoSeverity = "Validating";
            var job = await _provisioning.CreateAsync(
                result.SubscriptionId, result.ResourceGroupName, result.Region, parameters).ConfigureAwait(true);

            // 成功创建后首次 SSH 连接可直接预选密码凭据。目标 ResourceId 由服务层在提交时预拼，
            // 因此不需要等待 ARM 创建完成；若创建失败，这条无主映射不会暴露到任何 VM 上。
            if (passwordCredential is not null)
            {
                await _connectFlow.CredentialLibrary
                    .SetDefaultCredentialIdForVmAsync(job.ResourceId, passwordCredential.Id)
                    .ConfigureAwait(true);
            }

            InfoSeverity = null;
            InfoText = null;
            ShowJob(job);
            PendingApprovalJob = job.Status == JobStatus.WaitingApproval ? job : null;
            return Views.CreateVmSubmitOutcome.Ok();
        }
        catch (Exception ex)
        {
            // Exception.Message 来自服务 / ARM；ArmVmProvisioningExecutor 已经把 RequestFailedException
            // 摘成一句话（见 AzureErrorMessages），这里不会再收到原始 HTTP 请求/响应转储。
            var message = ex.Message;
            InfoSeverity = "Failed";
            InfoText = $"创建虚拟机失败：{message}";
            return Views.CreateVmSubmitOutcome.Failed(message);
        }
        finally
        {
            _jobStore.JobChanged -= OnProgressChanged;
        }
    }

    /// <summary>
    /// 对话框已经用推断/演示清单弹出来了，这里在后台把区域/规格目录换成真实数据。
    /// 两个目录服务失败时各自返回空列表并记录警告（见 <see cref="IRegionCatalog"/>/
    /// <see cref="IVmSizeCatalog"/> 的实现），这里再兜底 catch 一次纯粹是防御性的——
    /// 万一真的抛出来，也不能把已经打开的向导炸掉。
    /// </summary>
    /// <summary>
    /// 规格下拉默认选中"确认支持 Gen2、核数/内存最小"的那个——排第一的规格就是默认选中项
    /// （<see cref="Views.CreateVmWizardDialog.UpdateVmSizeOptions"/> 换列表后仍停在默认位置时
    /// 会重新选中索引 0）。之前是纯按名字字母排序，"Standard_A1_v2" 这种字母序靠前但其实是
    /// 仅支持 Gen1 的老规格反而容易被首选，跟现在市面上大多数默认 Gen2 的新镜像配不上。
    /// 不确定世代（<see cref="Views.VmSizeOption.HyperVGenerations"/> 为空）的排在"确认 Gen2"
    /// 之后、"确认仅 Gen1"之前——不能因为查不到能力就把它当成不兼容。
    /// </summary>
    private static int GenerationPriority(Views.VmSizeOption option) => option.HyperVGenerations switch
    {
        null or "" => 1,
        var gens when gens.Contains("V2", StringComparison.OrdinalIgnoreCase) => 0,
        _ => 2
    };

    /// <summary>
    /// 资源组下拉默认选中排第一的那个（同上，见 `UpdateResourceGroupOptions`），不能是 Azure 自己
    /// 自动生成的资源组——真实踩过的坑：一个订阅下第一次在某区域建虚拟网络，Azure 会自动开通
    /// Network Watcher 并生成一个专属资源组 `NetworkWatcherRG`，纯按字母/首次出现排序时它可能
    /// 排到用户自己建的资源组前面，导致默认选中的不是用户想要的那个。
    /// 这里只影响"默认选中谁"，不从下拉里隐藏它们——真要用还是能自己选。
    /// </summary>
    private static readonly string[] PlatformManagedResourceGroupPrefixes =
    [
        "NetworkWatcherRG", "DefaultResourceGroup-", "cleanup", "MC_", "Databricks-rg-",
        "AzureBackupRG_", "LogAnalyticsDefaultResources"
    ];

    private static bool IsPlatformManagedResourceGroup(string name) =>
        PlatformManagedResourceGroupPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private async Task LoadLiveCatalogsAsync(Views.CreateVmWizardDialog dialog, string subscriptionId)
    {
        // 资源组目录独立一个 try/catch：不能让它跟区域/规格那条链路的早退（比如区域查询为空）
        // 互相绊住——三个目录各查各的，谁失败都不该拖累另外两个已经查到的数据。
        try
        {
            var realResourceGroups = await _resourceGroupCatalog.GetAllAsync(subscriptionId);
            if (realResourceGroups.Count > 0)
            {
                dialog.UpdateResourceGroupOptions(realResourceGroups
                    .OrderBy(rg => IsPlatformManagedResourceGroup(rg.Name))
                    .ThenBy(rg => rg.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(rg => new Views.ResourceGroupOption(rg.Name, rg.Location))
                    .ToList());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建虚拟机向导：加载真实资源组目录失败，继续使用按已知虚拟机反推的清单");
        }

        try
        {
            var realRegions = await _regionCatalog.GetRegionsAsync(subscriptionId);
            if (realRegions.Count == 0)
            {
                return;
            }

            var regionOptions = realRegions
                .Select(region => new Views.RegionOption(region.Name, region.DisplayName))
                .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            dialog.UpdateRegionOptions(regionOptions);

            // 规格清单按"用户此刻实际选中的区域"续查一次——对话框可能已经用真实区域列表
            // 把默认选中项换过了，这里读的是换完之后的值。
            var region = dialog.CurrentRegionName;
            if (string.IsNullOrWhiteSpace(region))
            {
                return;
            }

            var realSizes = await _sizeCatalog.GetBySizeAsync(subscriptionId, region);
            if (realSizes.Count == 0)
            {
                return;
            }

            var sizeOptions = realSizes
                .Select(pair => new Views.VmSizeOption(
                    pair.Key, pair.Value.VCpus, pair.Value.MemoryMb, pair.Value.HyperVGenerations))
                .OrderBy(GenerationPriority)
                .ThenBy(option => option.VCpus ?? int.MaxValue)
                .ThenBy(option => option.MemoryMb ?? int.MaxValue)
                .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            dialog.UpdateVmSizeOptions(sizeOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建虚拟机向导：加载真实区域/规格目录失败，继续使用推断/演示清单");
        }
    }

    // ==== 行操作：全部经 Operation Engine（设计文档 §29）====

    [RelayCommand]
    private async Task StartAsync(VmSummary vm) => await SubmitPowerAsync(
        () => _power.StartAsync(vm), $"启动虚拟机 {vm.Name}");

    [RelayCommand]
    private async Task RestartAsync(VmSummary vm) => await SubmitPowerAsync(
        () => _power.RestartAsync(vm), $"重启虚拟机 {vm.Name}");

    [RelayCommand]
    private async Task PowerOffAsync(VmSummary vm)
    {
        var confirmed = Views.ConfirmDialog.Show(
            "关机",
            "虚拟机将停止，但计算资源仍保留分配。费用可能继续产生。",
            "关机", isDanger: true);
        if (!confirmed)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.PowerOffAsync(vm), $"关机 {vm.Name}");
    }

    [RelayCommand]
    private async Task DeallocateAsync(VmSummary vm)
    {
        var confirmed = Views.ConfirmDialog.Show(
            "解除分配",
            "虚拟机将停止并释放计算资源，停止计算计费（磁盘与保留 IP 可能继续计费）。",
            "停止并解除分配", isDanger: true);
        if (!confirmed)
        {
            return;
        }
        await SubmitPowerAsync(() => _power.DeallocateAsync(vm), $"解除分配 {vm.Name}");
    }

    /// <summary>
    /// 行菜单「删除虚拟机」：确认目标与要连带删除的关联资源，然后提交。
    /// </summary>
    /// <remarks>
    /// <b>删除一律会停在审批</b>：<c>DeleteVmHandler</c> 恒返回 <c>CannotBypass = true</c>，
    /// 即使用户把设置里的审批档调成「关闭」也拦得住。所以这里不需要（也不应该）自己判断
    /// 要不要审批 —— 那是引擎的事。
    /// </remarks>
    [RelayCommand]
    private void Delete(VmSummary vm)
    {
        // submitAsync 在对话框仍打开时跑 Validate + Impact 分析（一次真实 Azure 读取）——
        // 之前是对话框收完勾选就关闭，这次调用发生在关闭之后，用户完全看不到"正在读取"。
        var dialog = new Views.DeleteVmDialog(
            vm, (kinds, ct) => SubmitDeleteAsync(vm, kinds, ct))
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    private async Task<Views.DeleteVmSubmitOutcome> SubmitDeleteAsync(
        VmSummary vm, IReadOnlyCollection<VmLinkedResourceKind> kinds, CancellationToken ct)
    {
        InfoText = $"正在分析影响面：删除虚拟机 {vm.Name}…";
        InfoSeverity = "Validating";
        try
        {
            var job = await _power.DeleteAsync(vm, kinds).ConfigureAwait(true);
            ShowJob(job);
            PendingApprovalJob = job.Status == JobStatus.WaitingApproval ? job : null;
            return Views.DeleteVmSubmitOutcome.Ok();
        }
        catch (Exception ex)
        {
            InfoText = $"删除虚拟机 {vm.Name} 失败：{ex.Message}";
            InfoSeverity = "Failed";
            return Views.DeleteVmSubmitOutcome.Failed(ex.Message);
        }
    }

    /// <summary>停在 WaitingApproval 的 Job（§25 [Continue] 入口）。</summary>
    [ObservableProperty]
    private OperationJob? _pendingApprovalJob;

    /// <summary>审批通过并继续执行（审批后仍会走 Execute → WaitingAzure → Verify → Audit）。</summary>
    [RelayCommand]
    private void Approve()
    {
        if (PendingApprovalJob is null)
        {
            return;
        }

        var jobId = PendingApprovalJob.JobId;

        // §25：先把影响面摆给用户，由用户决定继续还是取消 —— 不能只给一个"批准"按钮。
        // 传入 submitAsync：真正的 Execute（删除虚拟机时是实打实的 ARM 调用，可能要几十秒）
        // 在对话框仍然打开时跑，失败就地报错、留在原地重试，不再"点了继续就消失、结果不知道"。
        var dialog = new Views.ImpactApprovalDialog(
            PendingApprovalJob, (onProgress, ct) => ApproveJobAsync(jobId, onProgress, ct))
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    private async Task<Views.ApprovalSubmitOutcome> ApproveJobAsync(
        Guid jobId, Action<string> onProgress, CancellationToken ct)
    {
        void OnProgressChanged(object? sender, OperationJob job)
        {
            if (job.JobId != jobId || string.IsNullOrEmpty(job.ProgressNote))
            {
                return;
            }

            // 同上：JobChanged 可能在非 UI 线程触发，直接调用 UI 回调会抛跨线程异常。
            Application.Current?.Dispatcher.BeginInvoke(() => onProgress(job.ProgressNote!));
        }

        // Execute 内部子步骤的进度（资源组→网络→网卡→虚拟机 / 虚拟机→网卡→磁盘）通过
        // JobChanged 广播出来——这里临时订阅一下，只为了把这台正在跑的 Job 的进度转发给对话框，
        // 跑完（无论成败）都要摘掉，不然会一直挂着别的 Job 的变化。
        _jobStore.JobChanged += OnProgressChanged;
        try
        {
            var job = await _engine.ApproveAsync(jobId, ct).ConfigureAwait(true);
            PendingApprovalJob = null;
            ShowJob(job);
            ApplyFilters();
            return Views.ApprovalSubmitOutcome.Ok();
        }
        catch (Exception ex)
        {
            return Views.ApprovalSubmitOutcome.Failed(ex.Message);
        }
        finally
        {
            _jobStore.JobChanged -= OnProgressChanged;
        }
    }

    private async Task SubmitPowerAsync(Func<Task<OperationJob>> submit, string display)
    {
        InfoText = $"正在提交：{display}…";
        InfoSeverity = "Validating";
        try
        {
            var job = await submit();
            ShowJob(job);

            // §25：破坏性电源操作会停在 WaitingApproval，必须给出审批出口
            PendingApprovalJob = job.Status == JobStatus.WaitingApproval ? job : null;
        }
        catch (Exception ex)
        {
            InfoText = $"{display} 失败：{ex.Message}";
            InfoSeverity = "Failed";
        }
    }

    /// <summary>手动关掉页面顶部那条操作反馈横幅——之前只有下一次操作才会覆盖它，
    /// 切走页面再切回来、甚至过了很久都还留着上一次的成功/失败文案，容易被当成"又出错了"。
    /// 顶栏"任务进行中"徽标已经承担了"正在跑的任务"这个职责，这条横幅只负责"最近一次操作的
    /// 即时反馈"，用户看过了就该能主动关掉。</summary>
    [RelayCommand]
    private void DismissInfo()
    {
        InfoText = null;
        InfoSeverity = null;
    }

    private void ShowJob(OperationJob job)
    {
        InfoSeverity = job.Status.ToString();
        InfoText = JobPresentation.Feedback(job);
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }

    private void OnJobChanged(object? sender, OperationJob job)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            // Job 完成后刷新行状态（Mock Handler 已改内存状态）
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed)
            {
                ShowJob(job);
                ApplyFilters();

                // 创建 / 删除都会改变清单成员；ApplyFilters 只重算当前内存切片，
                // 必须重新查询才能让新 VM 出现或被删 VM 消失。全局 Job 事件覆盖任务中心审批路径。
                if (job.OperationType is ComputeModule.OperationDelete or ComputeModule.OperationCreate)
                {
                    await RefreshAsync();
                }
            }
        });
    }
}
