using System.ComponentModel;

namespace CloudFlow.Modules.Compute.Models;

/// <summary>
/// VM 磁盘信息（OS Disk / Data Disk，设计文档 §26）。
/// SnapshotCount 变更通过 INPC 通知 UI。
/// </summary>
public sealed class VmDiskInfo : INotifyPropertyChanged
{
    public required string DiskId { get; init; }

    public required string Name { get; init; }

    /// <summary>OS Disk / Data Disk。</summary>
    public required string Type { get; init; }

    public required string Size { get; init; }

    /// <summary>SKU 层级，如 Premium SSD (P10)。</summary>
    public required string Tier { get; init; }

    public string Status { get; init; } = "Attached";

    private int _snapshotCount;

    public int SnapshotCount
    {
        get => _snapshotCount;
        set
        {
            if (_snapshotCount == value)
            {
                return;
            }
            _snapshotCount = value;
            OnPropertyChanged(nameof(SnapshotCount));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
