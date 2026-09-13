using System.Text;
using System.Text.RegularExpressions;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// vm.create：创建虚拟机（设计文档 v3.1 §87，v3.2 起支持按需新建资源组 / 虚拟网络 / 子网）。
/// </summary>
/// <remarks>
/// <para>
/// 预配范围：VM + NIC +（可选）公网 IP，OS 盘随 StorageProfile 隐式创建；资源组 / 虚拟网络 / 子网
/// 按<b>幂等 ensure-exists</b> 语义处理——不存在则用请求里给的参数新建，<b>已存在则完全不改动其配置</b>，
/// 只取其 Id 挂后续资源。这是为了支持"迁移到一个还没有任何网络基础设施的全新区域建机器"这类场景
/// （详见 v3.2 修订记录）。<b>仍不做</b> NSG 的创建。
/// </para>
/// <para><b>两条硬约束：</b></para>
/// <list type="number">
/// <item><b>密码不进载荷</b>：载荷随待审批请求落盘（jobs.json），密码进载荷就是明文落盘。
/// 密码方式下载荷只带 <see cref="PayloadCredentialId"/>（凭据库里的凭据 Id），
/// 明文由 App 层路由器在执行前解出、作为<b>不落盘的方法参数</b>传入（见 <see cref="IVmProvisioningExecutor"/>）。</item>
/// <item><b>Validate 不碰 Azure</b>：只做结构与格式校验。规格/镜像/资源组/网络的真实可用性与冲突
/// 由 UI 下拉与执行阶段的 ARM 错误兜底，这里只拦明显的拼写错误。</item>
/// </list>
/// <para>新建是<b>可逆</b>操作（建错了可以删掉），因此<b>不设 CannotBypass</b> ——
/// 与删除（不可逆、恒审批）刻意区别对待，见 §87.2/§87.3。</para>
/// </remarks>
public sealed partial class CreateVmHandler(
    IVmProvisioningExecutor executor,
    ILogger<CreateVmHandler> logger) : IOperationHandler
{
    public const string PayloadVmName = "vmName";
    public const string PayloadRegion = "region";
    public const string PayloadVirtualNetwork = "virtualNetwork";       // 名称，或完整 VNet Resource ID
    public const string PayloadVnetAddressSpace = "vnetAddressSpace";   // 仅虚拟网络不存在时用于新建
    public const string PayloadSubnetName = "subnetName";
    public const string PayloadSubnetAddressPrefix = "subnetAddressPrefix"; // 仅子网不存在时用于新建
    public const string PayloadVmSize = "vmSize";
    public const string PayloadImage = "image";
    public const string PayloadAdminUsername = "adminUsername";
    public const string PayloadAuthType = "authType";           // "ssh" | "password"
    public const string PayloadSshPublicKey = "sshPublicKey";   // 公钥不是秘密，可落盘
    public const string PayloadCredentialId = "credentialId";   // 密码方式的凭据 Id（明文不落盘）
    public const string PayloadPublicIp = "publicIp";           // "true" | "false"

    /// <summary>常用镜像清单（UI 下拉同源）。真实可用性随区域变化，由执行阶段的 ARM 错误兜底；
    /// 下拉本身可编辑，不在此列表内的自定义 URN 只做形状校验，见 <see cref="ImageUrnRegex"/>。</summary>
    public static readonly string[] SupportedImages =
    [
        "Canonical:ubuntu-24_04-lts:server:latest",
        "Canonical:ubuntu-22_04-lts:server:latest",
        "Debian:debian-12:12:latest",
        "RedHat:RHEL:9-lvm:latest",
        "MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure:latest",
        "MicrosoftWindowsServer:WindowsServer:2019-datacenter:latest",
        "MicrosoftWindowsDesktop:Windows-11:win11-23h2-ent:latest"
    ];

    /// <summary>Azure 计算资源名：字母数字或连字符，不能以连字符结尾，1-64 字符。</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}[a-zA-Z0-9]$", RegexOptions.Compiled)]
    private static partial Regex VmNameRegex();

    /// <summary>Azure 规格名的形状（不再要求命中固定白名单，真实可用性由区域动态目录 + ARM 兜底）。</summary>
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{2,63}$", RegexOptions.Compiled)]
    private static partial Regex VmSizeShapeRegex();

    /// <summary>镜像 URN 的形状：publisher:offer:sku:version，四段冒号分隔、每段非空。</summary>
    [GeneratedRegex(@"^[^:\s]+:[^:\s]+:[^:\s]+:[^:\s]+$", RegexOptions.Compiled)]
    private static partial Regex ImageUrnRegex();

    /// <summary>Azure 资源组名字符集：字母数字、下划线、圆括号、连字符、句点（不含 Azure 支持的全部 Unicode 范围，
    /// 只覆盖最常见输入；真正的名称冲突/非法字符由 ARM 报错兜底）。</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9._\-()]+$", RegexOptions.Compiled)]
    private static partial Regex ResourceGroupNameCharsetRegex();

    /// <summary>虚拟网络 / 子网名称：字母数字开头，其后允许字母数字、句点、下划线、连字符，1-80 字符。</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,79}$", RegexOptions.Compiled)]
    private static partial Regex NetworkResourceNameRegex();

    /// <summary>IPv4 CIDR（如 10.0.0.0/16）。子网划分是否与 VNet 内其它子网重叠留给 ARM 报错兜底。</summary>
    [GeneratedRegex(
        @"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}/(3[0-2]|[12]?\d)$",
        RegexOptions.Compiled)]
    private static partial Regex CidrRegex();

    public string OperationType => ComputeModule.OperationCreate;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var p = request.Payload;

        // 请求会在等待审批时落盘；任何疑似明文 Secret 都必须在进入后续流水线前拒绝。
        // sshPublicKey 是公开资料，credentialId 是保险库引用，二者不在此限制之列。
        var forbiddenKey = p.Keys.FirstOrDefault(key =>
            key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("token", StringComparison.OrdinalIgnoreCase));
        if (forbiddenKey is not null)
        {
            throw new OperationValidationException(
                $"创建虚拟机请求不得包含明文敏感字段「{forbiddenKey}」；密码必须使用 credentialId 引用凭据库。");
        }

        // ── 必填字段 ──
        foreach (var (key, label) in new[]
                 {
                     (PayloadVmName, "虚拟机名称"), (PayloadRegion, "区域"),
                     (PayloadVirtualNetwork, "虚拟网络"), (PayloadSubnetName, "子网名称"),
                     (PayloadVmSize, "规格"), (PayloadImage, "镜像"), (PayloadAdminUsername, "管理员用户名")
                 })
        {
            if (!p.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new OperationValidationException($"缺少必需参数：{label}。");
            }
        }

        // ── 资源组名称（从目标 ResourceId 解析；不存在则会在执行阶段自动新建，见类注释）──
        if (!ResourceIdentifier(request.ResourceId, out var resourceGroupName) ||
            string.IsNullOrWhiteSpace(resourceGroupName))
        {
            throw new OperationValidationException($"无法从目标解析资源组：{request.ResourceId}。");
        }

        if (resourceGroupName.Length is < 1 or > 90 || resourceGroupName.EndsWith('.') ||
            !ResourceGroupNameCharsetRegex().IsMatch(resourceGroupName))
        {
            throw new OperationValidationException(
                $"资源组名称「{resourceGroupName}」不合法：只能包含字母、数字、下划线、圆括号、连字符与句点，" +
                "不能以句点结尾，且不超过 90 字符。");
        }

        // ── 名称 ──
        var vmName = p[PayloadVmName];
        if (!VmNameRegex().IsMatch(vmName))
        {
            throw new OperationValidationException(
                $"虚拟机名称「{vmName}」不合法：只能包含字母、数字与连字符，不能以连字符开头或结尾。");
        }

        // ── 区域 ──
        // Azure ARM location 是短名称（如 koreacentral），不接受空格、路径分隔符或控制字符。
        var region = p[PayloadRegion];
        if (!Regex.IsMatch(region, "^[a-z0-9]{2,64}$", RegexOptions.CultureInvariant))
        {
            throw new OperationValidationException($"区域「{region}」不合法。请选择 Azure 区域短名称。");
        }

        // ── 虚拟网络（名称，或完整 Resource ID）──
        var vnet = p[PayloadVirtualNetwork];
        if (!vnet.Contains("/virtualNetworks/", StringComparison.OrdinalIgnoreCase) &&
            !NetworkResourceNameRegex().IsMatch(vnet))
        {
            throw new OperationValidationException(
                $"虚拟网络「{vnet}」不合法：请填写名称，或粘贴完整 Resource ID（…/virtualNetworks/…）。");
        }

        // ── 子网名称 ──
        var subnetName = p[PayloadSubnetName];
        if (!NetworkResourceNameRegex().IsMatch(subnetName))
        {
            throw new OperationValidationException($"子网名称「{subnetName}」不合法。");
        }

        // ── 地址段（仅目标不存在时才会真正用上，但格式必须先过关，避免执行到一半才报错）──
        if (p.TryGetValue(PayloadVnetAddressSpace, out var vnetCidr) && !string.IsNullOrWhiteSpace(vnetCidr) &&
            !CidrRegex().IsMatch(vnetCidr))
        {
            throw new OperationValidationException($"虚拟网络地址空间「{vnetCidr}」不是合法的 CIDR（如 10.0.0.0/16）。");
        }

        if (p.TryGetValue(PayloadSubnetAddressPrefix, out var subnetCidr) && !string.IsNullOrWhiteSpace(subnetCidr) &&
            !CidrRegex().IsMatch(subnetCidr))
        {
            throw new OperationValidationException($"子网地址段「{subnetCidr}」不是合法的 CIDR（如 10.0.0.0/24）。");
        }

        // ── 规格（形状校验；真实可用性由区域动态目录 + ARM 兜底）──
        if (!VmSizeShapeRegex().IsMatch(p[PayloadVmSize]))
        {
            throw new OperationValidationException($"规格「{p[PayloadVmSize]}」形状不合法。");
        }

        // ── 镜像（形状校验；真实可用性由 ARM 兜底）──
        if (!ImageUrnRegex().IsMatch(p[PayloadImage]))
        {
            throw new OperationValidationException(
                $"镜像「{p[PayloadImage]}」不合法：应为 publisher:offer:sku:version 四段格式。");
        }

        // ── 凭据方式 ──
        var authType = p.GetValueOrDefault(PayloadAuthType, "ssh");
        var isWindowsImage = p[PayloadImage].StartsWith("MicrosoftWindows", StringComparison.OrdinalIgnoreCase);
        if (isWindowsImage && vmName.Length > 15)
        {
            throw new OperationValidationException("Windows 虚拟机名称最多 15 个字符。");
        }

        switch (authType)
        {
            case "ssh":
                if (isWindowsImage)
                {
                    throw new OperationValidationException("Windows 镜像仅支持密码方式创建。");
                }

                if (string.IsNullOrWhiteSpace(p.GetValueOrDefault(PayloadSshPublicKey)))
                {
                    throw new OperationValidationException("SSH 方式必须提供公钥。");
                }

                break;

            case "password":
                if (!Guid.TryParse(p.GetValueOrDefault(PayloadCredentialId), out _))
                {
                    // 密码本体绝不进载荷 —— 只带凭据库里那条凭据的 Id
                    throw new OperationValidationException("密码方式必须提供凭据库凭据的 Id。");
                }

                break;

            default:
                throw new OperationValidationException($"未知的凭据方式：{authType}。");
        }

        return Task.CompletedTask;
    }

    public async Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var p = request.Payload;
        var withPublicIp = string.Equals(p.GetValueOrDefault(PayloadPublicIp), "true", StringComparison.OrdinalIgnoreCase);

        // Impact 阶段允许查 Provider（先例：共享子网 NSG 影响面就是在 Handler 里现查的）——
        // 如实告知这次会不会新建资源组/虚拟网络/子网，而不是等执行完才让用户发现多花了什么。
        var resourceGroupExists = await executor.ResourceGroupExistsAsync(request, ct).ConfigureAwait(false);

        var description = new StringBuilder();
        description.Append(
            $"在 {p.GetValueOrDefault(PayloadVirtualNetwork, string.Empty)} / {p.GetValueOrDefault(PayloadSubnetName, string.Empty)} " +
            $"下创建虚拟机 {p.GetValueOrDefault(PayloadVmName)}（{p.GetValueOrDefault(PayloadVmSize)}，" +
            $"{p.GetValueOrDefault(PayloadImage)}）。");

        var newlyCreated = 3; // VM + NIC + OS 盘
        if (withPublicIp)
        {
            description.Append(" 将同时创建公网 IP 与网卡。");
            newlyCreated++;
        }

        if (!resourceGroupExists)
        {
            description.Append(" 目标资源组不存在，将一并新建。");
            newlyCreated++;
        }

        // 虚拟网络/子网是否已存在无法在不实际连接 Provider 的情况下确定得比"资源组是否存在"更细
        // （需要先解出资源组、再看虚拟网络、再看子网），这里只做资源组这一层的读，网络层的"是否复用"
        // 结论以执行阶段的真实结果为准，但仍然把"可能新建网络资源"这句话如实带上，避免用户误以为
        // 影响范围与不带网络字段时完全一样。
        description.Append(" 若目标虚拟网络或子网不存在，也会按填写的地址段一并新建；已存在的虚拟网络/子网不会被改动，直接复用。");

        description.Append(" 资源创建后即开始计费，删除虚拟机不会自动删除数据盘之外的孤儿资源。");

        // 新建可逆（可删），不设 CannotBypass；Medium 风险按审批策略走「仅高危」档时会被放行
        return new ImpactAssessment
        {
            RequiresApproval = true,
            AffectedResources = newlyCreated,
            Description = description.ToString()
        };
    }

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        logger.LogInformation("执行 {Operation}：{Name} @ {Vnet}/{Subnet}",
            OperationType,
            request.Payload.GetValueOrDefault(PayloadVmName),
            request.Payload.GetValueOrDefault(PayloadVirtualNetwork),
            request.Payload.GetValueOrDefault(PayloadSubnetName));

        // 密码本体不经过这里：resolvePassword 委托由 App 层路由器在组装执行器时注入，
        // Handler 传 null（它不认识凭据库，也不该认识）。
        return await executor.CreateAsync(request, resolvePassword: null, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct) =>
        await executor.VmReadyAsync(request, ct).ConfigureAwait(false);

    /// <summary>从形如 …/resourceGroups/{rg}/… 的 Resource ID 里解析资源组名。</summary>
    private static bool ResourceIdentifier(string resourceId, out string? resourceGroupName)
    {
        var segments = resourceId.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                resourceGroupName = segments[i + 1];
                return true;
            }
        }

        resourceGroupName = null;
        return false;
    }
}
