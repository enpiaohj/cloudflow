using System.ComponentModel;
using System.Globalization;

namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// VM 清单条目（Inventory 视图模型，设计文档 §16）。
/// 数据来源：P1 接入后为 Azure Resource Graph；当前为 Mock。
/// </summary>
public sealed class VmSummary : INotifyPropertyChanged
{
    /// <summary>Azure Resource ID（唯一主键，设计文档 §30）。</summary>
    public required string ResourceId { get; init; }

    public required string Name { get; init; }

    /// <summary>计算机名 / FQDN。</summary>
    public string ComputerName { get; set; } = "";

    public required string SubscriptionId { get; init; }

    public string SubscriptionName { get; set; } = "";

    public string ResourceGroupName { get; set; } = "";

    public string Region { get; set; } = "";

    /// <summary>VM 规格，如 Standard_D4s_v5。</summary>
    public string VmSize { get; set; } = "";

    public VmOsType OsType { get; set; } = VmOsType.Windows;

    private VmPowerState _powerState;

    public VmPowerState PowerState
    {
        get => _powerState;
        set
        {
            if (_powerState == value)
            {
                return;
            }
            _powerState = value;
            OnPropertyChanged(nameof(PowerState));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>注意标记（如意外停机、备份缺失），与电源状态正交（概念图中的 Warning 状态）。</summary>
    private bool _hasWarning;

    public bool HasWarning
    {
        get => _hasWarning;
        set
        {
            if (_hasWarning == value)
            {
                return;
            }
            _hasWarning = value;
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>列表状态徽章文本：Warning 优先于电源状态显示（概念图 1）。</summary>
    public string StatusText => HasWarning ? "Warning" : PowerState.ToString();

    public string? PublicIp { get; set; }

    public string? PrivateIp { get; set; }

    /// <summary>列表 IP 列显示值：有公网 IP 用公网 IP，否则显示专用 IP（无网络数据时为 null）。</summary>
    public string? IpDisplay => !string.IsNullOrEmpty(PublicIp) ? PublicIp : PrivateIp;

    /// <summary>
    /// 名称单元格第二行：计算机名与资源名相同（多数 VM 如此）时留空，
    /// 避免列表里出现 "appscloud / appscloud" 这样的重复行。
    /// </summary>
    public string ComputerNameDisplay =>
        string.Equals(ComputerName, Name, StringComparison.OrdinalIgnoreCase) ? "" : ComputerName;

    /// <summary>IP 列提示：区分公网 / 专用，避免用户把专用地址当作可直连地址。无网络数据时为空。</summary>
    public string IpKind => !string.IsNullOrEmpty(PublicIp) ? "公网 IP"
        : string.IsNullOrEmpty(PrivateIp) ? ""
        : "专用 IP";

    /// <summary>「操作系统」列显示值。</summary>
    public string OsTypeText => OsType == VmOsType.Linux ? "Linux" : "Windows";

    /// <summary>发行版名（来自 VM Agent 上报，如 "ubuntu"、"Windows"）；未上报时为空。</summary>
    public string OsName { get; set; } = "";

    /// <summary>发行版版本（如 "24.04"）；未上报时为空。</summary>
    public string OsVersion { get; set; } = "";

    /// <summary>市场镜像的 offer（如 "ubuntu-24_04-lts"），VM Agent 未上报时作为兜底。</summary>
    public string OsImageOffer { get; set; } = "";

    /// <summary>
    /// VM 资源的创建时间。Resource Graph 返回 ISO 8601（UTC）；解析失败则为 null，
    /// 此时界面不显示该行 —— 不用 <see cref="DateTimeOffset.MinValue"/> 之类的哨兵值冒充。
    /// </summary>
    public DateTimeOffset? TimeCreated { get; set; }

    /// <summary>创建时间的展示文本，与 Azure 门户一致（「2026/9/4 UTC 06:54」）。无值时为 "—"。</summary>
    public string TimeCreatedText => TimeCreated is { } t
        ? t.ToUniversalTime().ToString("yyyy/M/d 'UTC' HH:mm", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>
    /// 是否启用休眠（<c>properties.additionalCapabilities.hibernationEnabled</c>）。
    /// null = 该 VM 未返回此属性（老规格 / 老资源），与 false（明确已禁用）不是一回事。
    /// </summary>
    public bool? HibernationEnabled { get; set; }

    /// <summary>休眠的展示文本。null 时返回 "—"，仅在该行确实要显示时才渲染。</summary>
    public string HibernationText => HibernationEnabled switch
    {
        true => "已启用",
        false => "已禁用",
        _ => "—"
    };

    /// <summary>
    /// 「操作系统」单元格第二行：优先用 VM Agent 上报的发行版与版本（"ubuntu 24.04"），
    /// 没有则退回镜像 offer；都没有时留空（只显示 Linux / Windows）。
    /// </summary>
    public string OsVersionDisplay => !string.IsNullOrEmpty(OsName)
        ? $"{OsName} {OsVersion}".Trim()
        : OsImageOffer;

    /// <summary>
    /// 内存总量（MB）。Azure 的 VM 对象本身不含内存字段，只能由规格（<see cref="VmSize"/>）
    /// 经规格目录推导；推导不到时为 null，界面显示 "—"（不猜、不填 0）。
    /// </summary>
    public int? MemoryMb { get; set; }

    /// <summary>「内存」显示值，如 "4 GiB"；未知时 "—"。</summary>
    public string MemoryText => MemoryMb is not { } mb
        ? "—"
        : mb % 1024 == 0 ? $"{mb / 1024} GiB" : $"{mb / 1024.0:0.#} GiB";

    /// <summary>vCPU 核数。与内存同源（由规格推导），推导不到时为 null。</summary>
    public int? VCpuCount { get; set; }

    /// <summary>「vCPU」显示值，如 "2"；未知时 "—"。</summary>
    public string VCpuText => VCpuCount?.ToString() ?? "—";

    private bool _isChecked;

    /// <summary>
    /// 列表行勾选（批量连接的选择状态）。
    /// </summary>
    /// <remarks>
    /// 必须发变更通知：<b>表头「本页全选」是程序化改这个属性的</b>，
    /// 没有通知的话勾选框在界面上不会跟着变 —— 点到全选却什么都没发生。
    /// 同理，「连接选中 (N)」的计数也依赖它。
    /// </remarks>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }
    }

    private double? _cpuPercent;

    /// <summary>
    /// CPU 使用率（%），无数据时为 null（显示 "—"）。
    /// 列表页只对当前可见页异步拉取指标，值可能在绑定之后才到达，因此需要变更通知。
    /// </summary>
    public double? CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (_cpuPercent == value)
            {
                return;
            }
            _cpuPercent = value;
            OnPropertyChanged(nameof(CpuPercent));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
