using System.IO;
using System.Windows;
using CloudFlow.App.ViewModels;
using CloudFlow.App.Views;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Terminal.Security;
using CloudFlow.Terminal.Ssh;

namespace CloudFlow.App.Infrastructure;

/// <summary>一次连接请求：两条互斥的出口，与 <see cref="SshCredentialInput"/> 的语义一一对应。</summary>
public sealed class SshConnectRequest
{
    /// <summary>非 <c>null</c> = 用凭据库里这一条。</summary>
    public Guid? CredentialId { get; init; }

    /// <summary>临时输入；<see cref="CredentialId"/> 为 <c>null</c> 时必填。</summary>
    public SshCredentialInput? TransientInput { get; init; }
}

/// <summary>构建连接参数的结果。<see cref="FailureMessage"/> 是可直接展示给用户的中文。</summary>
public sealed record SshPrepareResult(SshConnectionOptions? Options, string? FailureMessage)
{
    public static SshPrepareResult Ok(SshConnectionOptions options) => new(options, null);

    public static SshPrepareResult Fail(string message) => new(null, message);
}

/// <summary>一次连接尝试的结果。</summary>
public sealed record SshConnectResult(bool Connected, string? FailureMessage)
{
    public static SshConnectResult Ok() => new(true, null);

    public static SshConnectResult Fail(string message) => new(false, message);
}

/// <summary>
/// 单台 SSH 连接的唯一实现：<b>VM 详情页与 VM 列表都调它</b>。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来的直接理由：连接入口从 1 个变成 3 个（详情页、列表行菜单、列表批量）。
/// 若各写一遍，下面这三段必然分叉 ——
/// ① 临时输入落库、② 从保险库解析凭据、③ 记录"最近使用"。
/// 分叉的后果不是"多写几行"，而是<b>同样的操作在不同入口表现不一致</b>，且只有真机才看得出来。
/// </para>
/// <para>
/// 本类<b>不弹凭据对话框</b>：选哪条凭据是调用方的事（对话框、批量计划各不相同），
/// 这里只负责"给定请求，把连接建起来"。
/// </para>
/// </remarks>
public sealed class SshConnectFlow
{
    private readonly SshConnectionService _ssh;
    private readonly TerminalPanelViewModel _panel;

    public SshConnectFlow(SshConnectionService ssh, TerminalPanelViewModel panel)
    {
        _ssh = ssh;
        _panel = panel;
    }

    /// <summary>凭据库 —— 调用方构造凭据对话框时要把它传进去。</summary>
    public SshCredentialService CredentialLibrary => _ssh.CredentialLibrary;

    /// <summary>主机指纹记录 —— 批量编排要用它（探测轮查"有没有记录"、被接受后写回）。</summary>
    public KnownHostStore KnownHosts => _ssh.KnownHosts;

    /// <summary>
    /// 交互式主机指纹策略（首次记录需确认、指纹变化强警告）。
    /// 放在这里是为了让多个入口共用<b>同一个</b>策略实现 —— 指纹确认是安全路径，不该有第二份。
    /// </summary>
    public SshHostKeyPolicy CreateInteractivePolicy() =>
        new(_ssh.KnownHosts, key =>
        {
            var confirm = new HostKeyConfirmDialog(key)
            {
                Owner = Application.Current?.MainWindow
            };
            return Task.FromResult(confirm.ShowDialog() is true);
        });

    /// <summary>单台连接：构建参数 → 交给底部终端面板。</summary>
    public async Task<SshConnectResult> ConnectAsync(
        VmSummary vm, string host, SshConnectRequest request, SshHostKeyPolicy policy, int port = 22)
    {
        var prepared = await PrepareAsync(request, host, port, policy, vm.ResourceId);
        if (prepared.Options is null)
        {
            return SshConnectResult.Fail(prepared.FailureMessage!);
        }

        _panel.OpenOrActivate(vm, host, prepared.Options);
        return SshConnectResult.Ok();
    }

    /// <summary>
    /// 构建连接参数，<b>不建会话</b>。
    /// </summary>
    /// <remarks>
    /// 批量路径用这个方法拿到一份"模板"，再按目标逐台覆写 Host ——
    /// 那样凭据只从保险库解析一次，而不是 N 次（每次都是整文件读 + DPAPI 解密）。
    /// </remarks>
    public async Task<SshPrepareResult> PrepareAsync(
        SshConnectRequest request, string host, int port, SshHostKeyPolicy policy,
        string? vmResourceId = null)
    {
        var credentialId = request.CredentialId;

        // 临时输入若勾了入库，就先落库，随后统一走凭据库那条路 ——
        // 存进去的和实际拿来连的必须是同一条记录，否则用户会以为连的是"刚存的那条"。
        if (credentialId is null && request.TransientInput is { SaveCredential: true } transient)
        {
            try
            {
                credentialId = (await SaveTransientAsync(transient)).Id;
            }
            catch (Exception ex)
            {
                // 保存失败就中止，不继续连接：静默连上会让用户以为已经存好了。
                // 这里的异常来自 SshCredentialService 的校验（名称重复、缺密码 / 私钥），
                // 消息本就是写给用户看的，不含任何秘密。
                return SshPrepareResult.Fail($"凭据未能存入凭据库：{ex.Message}");
            }
        }

        SshConnectionOptions? options = credentialId is Guid id
            ? await _ssh.BuildOptionsFromCredentialAsync(id, host, port, policy)
            : await _ssh.BuildTransientOptionsAsync(request.TransientInput!, host, port, policy);

        if (options is null)
        {
            // 库里的密文解不开（保险库被删 / 来自他机）：明确告知，绝不带着空密码去连
            return SshPrepareResult.Fail(
                "该凭据的密文无法解密（保险库文件可能已被删除，或来自其他机器）。" +
                "请改用临时输入，或在设置页「凭据管理」里重新保存这条凭据。");
        }

        // 记录"这次选了它"，供下次打开对话框时预选。刻意不等到连接成功才记：
        // 会话是异步建立的，成功与否由终端面板自己报告，
        // 为一个预选去给 OpenOrActivate 加一条成功回调并不划算。
        if (credentialId is Guid usedId)
        {
            await _ssh.CredentialLibrary.MarkUsedAsync(usedId);

            // 按 VM 记住（用户已定口径）：这台机器下次进连接框就预选它，直到用户改选。
            // ⚠️ 只在**真的用了库里某条凭据**时记。临时输入连接一次不该把这台机器已经记好的
            // 连接配置抹掉 —— 临时输入是"这一次不落盘"，不是"以后都用临时的"。
            if (!string.IsNullOrWhiteSpace(vmResourceId))
            {
                await _ssh.CredentialLibrary.SetDefaultCredentialIdForVmAsync(vmResourceId, usedId);
            }
        }

        return SshPrepareResult.Ok(options);
    }

    /// <summary>
    /// 把临时输入存进全局凭据库。
    /// </summary>
    /// <remarks>
    /// 校验一律交给 <see cref="SshCredentialService"/> —— 它抛出的消息本就是给用户看的，
    /// 这里只做字段搬运，不在 UI 侧再实现一遍，否则两处规则迟早会分叉。
    /// </remarks>
    private async Task<SshCredential> SaveTransientAsync(SshCredentialInput input)
    {
        var isPassword = input.AuthType == SshCredentialAuthType.Password;

        var draft = new SshCredential
        {
            Name = input.Name ?? string.Empty,
            Username = input.Username,
            AuthType = isPassword ? SshAuthType.Password : SshAuthType.PrivateKey,
            PrivateKeyPath = isPassword ? null : input.PrivateKeyPath,
        };

        var keyBody = isPassword || input.PrivateKeyPath is null
            ? null
            : await File.ReadAllTextAsync(input.PrivateKeyPath);

        // 私钥方式下 secret 槽位放的是 passphrase（见 SshCredential.SecretReference 的说明）
        return await _ssh.CredentialLibrary.CreateAsync(
            draft, isPassword ? input.Password : input.Passphrase, keyBody);
    }
}
