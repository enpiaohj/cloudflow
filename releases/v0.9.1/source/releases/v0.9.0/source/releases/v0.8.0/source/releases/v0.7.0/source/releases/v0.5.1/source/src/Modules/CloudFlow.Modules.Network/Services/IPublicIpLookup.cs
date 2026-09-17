namespace CloudFlow.Modules.Network.Services;

/// <summary>
/// 查询**运行 CloudFlow 的这台电脑**的出口公网 IP。
///
/// 与 <see cref="ICurrentIpProvider"/> 的关系：这个接口只负责"去查"，不判断登录态、
/// 也不管设置里有没有关掉自动查询 —— 那些是 App 层分流的职责。
///
/// 与"虚拟机出站连通性"（<see cref="IVmNetworkService.GetOutboundAsync"/>）是两件毫不相干的事：
/// 这里一次 HTTP 请求就够，那边要遍历整个 Azure 资源图。前者是"我这台电脑的出口"，
/// 后者是"某台 Azure 虚拟机的出口"。
/// </summary>
public interface IPublicIpLookup
{
    /// <summary>
    /// 当前出口公网 IP。**任何失败都返回 null，绝不编造**：
    /// 拿一个猜出来的地址去建 NSG 规则，用户会以为自己已经收紧了来源。
    /// </summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct = default);
}
