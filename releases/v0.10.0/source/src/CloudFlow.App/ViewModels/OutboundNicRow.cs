namespace CloudFlow.App.ViewModels;

/// <summary>
/// 网络标签页「出站连接」卡片里的单块网卡明细（多网卡时逐块列出）。
/// 出口 IP 的文案规则与 VM 级完全一致，统一走
/// <see cref="CloudFlow.Modules.Network.Models.OutboundText"/>。
/// </summary>
public sealed record OutboundNicRow
{
    public required string NicName { get; init; }

    public bool IsPrimary { get; init; }

    public required string TypeLabel { get; init; }

    /// <summary>徽章配色键（CfStatusBrushConverter）。</summary>
    public required string Badge { get; init; }

    public required string IpText { get; init; }

    public required string Explanation { get; init; }

    /// <summary>所在子网（多网卡常跨子网，不写清就分不出哪块卡走哪条路）。</summary>
    public string SubnetText { get; init; } = "";

    public bool RoutedToVirtualAppliance { get; init; }

    public bool HasSubnet => !string.IsNullOrEmpty(SubnetText);

    /// <summary>网卡标题：网卡名 + 主网卡标记。</summary>
    public string Title => IsPrimary ? $"{NicName}（主网卡）" : NicName;
}
