using System.ComponentModel;

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

    /// <summary>列表行勾选（P2 批量操作的前端状态）。</summary>
    public bool IsChecked { get; set; }

    /// <summary>CPU 使用率（%），无数据时为 null（显示 "—"）。</summary>
    public double? CpuPercent { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
