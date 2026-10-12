namespace CloudFlow.Modules.Network.Models;

/// <summary>
/// 出站连通性的文案与配色键的**唯一来源**。
///
/// 放在模块层而不是 UI 层，有两个原因：
/// 1. 「查不到」与「确定没有」的分界是模型语义的一部分（见 <see cref="VmOutboundConnectivity.DiscoverableByArm"/>），
///    不是渲染细节 —— 换个界面它也必须成立。
/// 2. 这条规则必须能被断言。放在 WPF 工程里就只能靠肉眼在页面上看，而它恰恰是最不该出错的一条。
/// </summary>
public static class OutboundText
{
    /// <summary>
    /// 出站方式的中文标签。VM 级结论与逐网卡明细共用，各写一份必然漂移。
    /// </summary>
    public static string TypeLabel(OutboundConnectivityType type) => type switch
    {
        OutboundConnectivityType.NatGateway => "NAT Gateway",
        OutboundConnectivityType.UserDefinedRoute => "UDR → 网络虚拟设备",
        OutboundConnectivityType.InstancePublicIp => "实例级公网 IP",
        OutboundConnectivityType.LoadBalancerOutboundRule => "负载均衡器出站规则",
        OutboundConnectivityType.LoadBalancerImplicit => "负载均衡器隐式出站",
        OutboundConnectivityType.DefaultOutboundAccess => "默认出站访问",
        OutboundConnectivityType.None => "无公网出口",
        OutboundConnectivityType.Mixed => "多网卡不一致",
        _ => "未知",
    };

    /// <summary>
    /// 出口 IP 的展示文案 —— **这里是"查不到"与"确定没有"的分界点**。
    ///
    /// <c>DiscoverableByArm == false</c> 时必须说「未知（ARM 无法获取）」：
    /// 默认出站访问、以及指向无法解析的网络虚拟设备的 UDR，都属于
    /// "确实存在公网出口，但地址拿不到"。这两种情况渲染成 "—" 或「无公网出口」，
    /// 用户会看到一行和"端口已收紧"一模一样的输出，进而以为不需要再配 NSG 规则。
    /// </summary>
    public static string DescribeIp(VmOutboundConnectivity outbound)
    {
        IReadOnlyList<string> addresses = string.IsNullOrWhiteSpace(outbound.OutboundIp)
            ? outbound.OutboundCidrs
            : [outbound.OutboundIp, .. outbound.OutboundCidrs];

        if (addresses.Count > 0)
        {
            return string.Join(" / ", addresses);
        }

        if (!outbound.DiscoverableByArm)
        {
            return UnknownOutboundIp;
        }

        // 走到这里才是真的确定：能查到、也确实没有地址
        return outbound.Type == OutboundConnectivityType.None ? NoPublicEgress : "—";
    }

    /// <summary>「出口 IP 存在但 ARM 拿不到」的固定文案。UI 与测试都引用这一份。</summary>
    public const string UnknownOutboundIp = "未知（ARM 无法获取）";

    /// <summary>「确定没有公网出口」的固定文案。</summary>
    public const string NoPublicEgress = "无公网出口";

    /// <summary>徽章配色键。复用 <c>CfStatusBrushConverter</c> 既有状态色，不新增一套状态语义。</summary>
    public static string BadgeOf(VmOutboundConnectivity outbound) => outbound.Type switch
    {
        // 显式且可定位的出口方式：绿色（与 NSG「受保护」同一语义层级 —— 有明确配置可查）
        OutboundConnectivityType.NatGateway
            or OutboundConnectivityType.InstancePublicIp
            or OutboundConnectivityType.LoadBalancerOutboundRule => "Protected",

        // 经网络虚拟设备 / 网关绕行：紫色（表示"不在本机网卡上直接可见"）
        OutboundConnectivityType.UserDefinedRoute => "Subnet",

        // 隐式出站：非生产级做法，值得提醒
        OutboundConnectivityType.LoadBalancerImplicit
            or OutboundConnectivityType.DefaultOutboundAccess => "Unprotected",

        OutboundConnectivityType.Mixed => "Warning",

        // 确定没有公网出口是中性事实，不是故障
        OutboundConnectivityType.None => "Stopped",

        _ => "Unknown"
    };

    /// <summary>
    /// 被更高优先级压制、但仍配置着的出站方式。
    /// 静默丢掉会让用户以为"这里只配过一种"，而多方法共存时的叠加行为微软并未完整定义，
    /// 因此全部列出并要求人工复核。
    /// </summary>
    public static string DescribeSuppressed(IReadOnlyList<OutboundCandidate> candidates)
    {
        var suppressed = candidates.Where(c => !c.IsEffective).ToList();
        if (suppressed.Count == 0)
        {
            return "";
        }

        var parts = suppressed.Select(c =>
        {
            var label = TypeLabel(c.Type);
            if (c.Failed)
            {
                var reason = string.IsNullOrWhiteSpace(c.Reason) ? "" : "：" + c.Reason;
                return $"{label}（读取失败，出口地址未知{reason}）";
            }

            var detail = c.Reason ?? c.Source;
            return string.IsNullOrWhiteSpace(detail) ? label : $"{label}：{detail}";
        });

        return "同时检测到但未生效（被更高优先级压制）：" + string.Join("；", parts);
    }
}
