using Wpf.Ui.Controls;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// Azure 资源类型 → 界面展示用的中文名称与图标。
/// </summary>
/// <remarks>
/// <para>
/// Resource Graph 返回的是 <c>microsoft.network/networkwatchers</c> 这类小写的内部类型字符串，
/// 直接铺在列表里读起来像日志而不是产品界面。名称对齐 Azure 门户中文版的叫法；
/// 不认识的类型退回去掉 <c>microsoft.</c> 前缀后的原始写法，原始字符串始终留在 ToolTip 里。
/// </para>
/// <para>
/// 图标只用码位在 U+FFFF 以内的符号：WPF-UI 3.0.5 把更高码位截断成 16 位，
/// 会渲染成"á"一类的乱码（见 XamlSymbolLiteralTests 的码位检查）。
/// </para>
/// </remarks>
public static class AzureResourceTypeCatalog
{
    public readonly record struct Entry(string DisplayName, SymbolRegular Symbol);

    private static readonly Dictionary<string, Entry> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft.compute/virtualmachines"] = new("虚拟机", SymbolRegular.Desktop24),
        ["microsoft.compute/virtualmachines/extensions"] = new("虚拟机扩展", SymbolRegular.PuzzlePiece24),
        ["microsoft.compute/disks"] = new("磁盘", SymbolRegular.Storage24),
        ["microsoft.compute/snapshots"] = new("快照", SymbolRegular.Storage24),
        ["microsoft.compute/images"] = new("映像", SymbolRegular.Cube24),
        ["microsoft.compute/availabilitysets"] = new("可用性集", SymbolRegular.Server24),
        ["microsoft.compute/sshpublickeys"] = new("SSH 公钥", SymbolRegular.Key24),
        ["microsoft.network/virtualnetworks"] = new("虚拟网络", SymbolRegular.Diagram24),
        ["microsoft.network/networkinterfaces"] = new("网络接口", SymbolRegular.PlugConnected24),
        ["microsoft.network/publicipaddresses"] = new("公共 IP 地址", SymbolRegular.Globe24),
        ["microsoft.network/networksecuritygroups"] = new("网络安全组", SymbolRegular.Shield24),
        ["microsoft.network/networkwatchers"] = new("网络观察程序", SymbolRegular.Eye24),
        ["microsoft.network/loadbalancers"] = new("负载均衡器", SymbolRegular.ServerMultiple20),
        ["microsoft.network/routetables"] = new("路由表", SymbolRegular.Router24),
        ["microsoft.network/natgateways"] = new("NAT 网关", SymbolRegular.Router24),
        ["microsoft.network/applicationgateways"] = new("应用程序网关", SymbolRegular.Router24),
        ["microsoft.network/bastionhosts"] = new("Bastion", SymbolRegular.ShieldLock24),
        ["microsoft.network/privateendpoints"] = new("专用终结点", SymbolRegular.Link24),
        ["microsoft.network/dnszones"] = new("DNS 区域", SymbolRegular.Globe24),
        ["microsoft.network/privatednszones"] = new("专用 DNS 区域", SymbolRegular.Globe24),
        ["microsoft.storage/storageaccounts"] = new("存储账户", SymbolRegular.Database24),
        ["microsoft.keyvault/vaults"] = new("密钥保管库", SymbolRegular.Key24),
        ["microsoft.cognitiveservices/accounts"] = new("Azure AI 服务", SymbolRegular.BrainCircuit24),
        ["microsoft.cognitiveservices/accounts/projects"] = new("Azure AI 项目", SymbolRegular.BrainCircuit24),
        ["microsoft.web/sites"] = new("应用服务", SymbolRegular.AppGeneric24),
        ["microsoft.web/serverfarms"] = new("应用服务计划", SymbolRegular.Server24),
        ["microsoft.sql/servers"] = new("SQL 服务器", SymbolRegular.Database24),
        ["microsoft.sql/servers/databases"] = new("SQL 数据库", SymbolRegular.Database24),
        ["microsoft.dbforpostgresql/flexibleservers"] = new("PostgreSQL 灵活服务器", SymbolRegular.Database24),
        ["microsoft.dbformysql/flexibleservers"] = new("MySQL 灵活服务器", SymbolRegular.Database24),
        ["microsoft.containerregistry/registries"] = new("容器注册表", SymbolRegular.BoxMultiple24),
        ["microsoft.containerservice/managedclusters"] = new("Kubernetes 服务", SymbolRegular.CubeMultiple24),
        ["microsoft.operationalinsights/workspaces"] = new("Log Analytics 工作区", SymbolRegular.DataUsage24),
        ["microsoft.insights/components"] = new("Application Insights", SymbolRegular.ArrowTrendingLines24),
        ["microsoft.insights/actiongroups"] = new("操作组", SymbolRegular.Flash24),
        ["microsoft.insights/metricalerts"] = new("指标警报规则", SymbolRegular.Warning24),
        ["microsoft.alertsmanagement/smartdetectoralertrules"] = new("智能检测警报规则", SymbolRegular.Warning24),
        ["microsoft.managedidentity/userassignedidentities"] = new("托管标识", SymbolRegular.Person24),
        ["microsoft.automation/automationaccounts"] = new("自动化账户", SymbolRegular.Flash24),
        ["microsoft.recoveryservices/vaults"] = new("恢复服务保管库", SymbolRegular.ShieldCheckmark24),
        ["microsoft.devtestlab/schedules"] = new("自动关机计划", SymbolRegular.Clock24)
    };

    public static Entry Describe(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return new Entry("未知类型", SymbolRegular.Cube24);
        }

        return Known.TryGetValue(type, out var entry)
            ? entry
            : new Entry(Fallback(type), SymbolRegular.Cube24);
    }

    private static string Fallback(string type) =>
        type.StartsWith("microsoft.", StringComparison.OrdinalIgnoreCase) ? type["microsoft.".Length..] : type;
}
