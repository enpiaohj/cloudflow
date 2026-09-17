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
/// vm.create：创建虚拟机（设计文档 v3.1 §87）。
/// </summary>
/// <remarks>
/// <para>
/// 预配范围刻意收窄：VM + NIC +（可选）公网 IP，OS 盘随 StorageProfile 隐式创建；
/// <b>不做</b>资源组 / VNet / 子网 / NSG 的创建 —— 新建的机器挂到<b>已存在</b>的子网。
/// </para>
/// <para><b>两条硬约束：</b></para>
/// <list type="number">
/// <item><b>密码不进载荷</b>：载荷随待审批请求落盘（jobs.json），密码进载荷就是明文落盘。
/// 密码方式下载荷只带 <see cref="PayloadCredentialId"/>（凭据库里的凭据 Id），
/// 明文由 App 层路由器在执行前解出、作为<b>不落盘的方法参数</b>传入（见 <see cref="IVmProvisioningExecutor"/>）。</item>
/// <item><b>Validate 不碰 Azure</b>：只做结构与白名单校验。规格/镜像走白名单 ——
/// 真实可用性随区域变化，由 UI 下拉与执行阶段的 ARM 错误兜底，这里只拦明显的拼写错误。</item>
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
    public const string PayloadSubnetId = "subnetId";
    public const string PayloadVmSize = "vmSize";
    public const string PayloadImage = "image";
    public const string PayloadAdminUsername = "adminUsername";
    public const string PayloadAuthType = "authType";           // "ssh" | "password"
    public const string PayloadSshPublicKey = "sshPublicKey";   // 公钥不是秘密，可落盘
    public const string PayloadCredentialId = "credentialId";   // 密码方式的凭据 Id（明文不落盘）
    public const string PayloadPublicIp = "publicIp";           // "true" | "false"

    /// <summary>常用镜像白名单（UI 下拉同源）。执行阶段的区域可用性由 ARM 错误兜底。</summary>
    public static readonly string[] SupportedImages =
    [
        "Canonical:ubuntu-24_04-lts:server:latest",
        "Canonical:ubuntu-22_04-lts:server:latest",
        "MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure:latest"
    ];

    /// <summary>常用规格白名单（与 <see cref="ResizeVmHandler.SupportedSizes"/> 同源思路）。</summary>
    public static readonly string[] SupportedSizes =
    [
        "Standard_B1s",
        "Standard_B2s",
        "Standard_B2ms",
        "Standard_D2s_v5"
    ];

    /// <summary>Azure 计算资源名：字母数字或连字符，不能以连字符结尾，1-64 字符。</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}[a-zA-Z0-9]$", RegexOptions.Compiled)]
    private static partial Regex VmNameRegex();

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
                     (PayloadSubnetId, "子网"), (PayloadVmSize, "规格"),
                     (PayloadImage, "镜像"), (PayloadAdminUsername, "管理员用户名")
                 })
        {
            if (!p.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new OperationValidationException($"缺少必需参数：{label}。");
            }
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

        // ── 子网必须是合法的 Azure Resource ID ──
        if (!p[PayloadSubnetId].Contains("/subnets/", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException("子网参数必须是完整的子网 Resource ID（…/virtualNetworks/…/subnets/…）。");
        }

        // ── 白名单 ──
        if (!SupportedSizes.Contains(p[PayloadVmSize], StringComparer.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"规格 {p[PayloadVmSize]} 不在常用列表内。");
        }

        if (!SupportedImages.Contains(p[PayloadImage], StringComparer.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"镜像 {p[PayloadImage]} 不在常用列表内。");
        }

        // ── 凭据方式 ──
        var authType = p.GetValueOrDefault(PayloadAuthType, "ssh");
        var isWindowsImage = p[PayloadImage].StartsWith("MicrosoftWindowsServer:", StringComparison.OrdinalIgnoreCase);
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

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var p = request.Payload;
        var withPublicIp = string.Equals(p.GetValueOrDefault(PayloadPublicIp), "true", StringComparison.OrdinalIgnoreCase);

        var description = new StringBuilder();
        description.Append(
            $"在 {p.GetValueOrDefault(PayloadSubnetId, string.Empty).Split('/')[^3]} 子网中创建虚拟机 " +
            $"{p.GetValueOrDefault(PayloadVmName)}（{p.GetValueOrDefault(PayloadVmSize)}，" +
            $"{p.GetValueOrDefault(PayloadImage)}）。");

        if (withPublicIp)
        {
            description.Append(" 将同时创建公网 IP 与网卡。");
        }

        description.Append(" 资源创建后即开始计费，删除虚拟机不会自动删除数据盘之外的孤儿资源。");

        // 新建可逆（可删），不设 CannotBypass；Medium 风险按审批策略走「仅高危」档时会被放行
        return Task.FromResult(new ImpactAssessment
        {
            RequiresApproval = true,
            AffectedResources = withPublicIp ? 4 : 3,
            Description = description.ToString()
        });
    }

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        logger.LogInformation("执行 {Operation}：{Name} @ {Subnet}",
            OperationType,
            request.Payload.GetValueOrDefault(PayloadVmName),
            request.Payload.GetValueOrDefault(PayloadSubnetId));

        // 密码本体不经过这里：resolvePassword 委托由 App 层路由器在组装执行器时注入，
        // Handler 传 null（它不认识凭据库，也不该认识）。
        return await executor.CreateAsync(request, resolvePassword: null, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct) =>
        await executor.VmReadyAsync(request, ct).ConfigureAwait(false);
}
