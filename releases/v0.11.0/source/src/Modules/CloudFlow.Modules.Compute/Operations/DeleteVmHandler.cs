using System.Text;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// vm.delete：删除虚拟机（设计文档 v3.1 §87）。
/// </summary>
/// <remarks>
/// <para>
/// <b>不继承 <see cref="VmPowerHandlerBase"/></b> —— 那个基类的 Verify 绑定 <c>VmPowerState</c>，
/// 而删除之后没有状态可取：删除的 Verify 是「资源已消失」。照 <see cref="ResizeVmHandler"/> 独立实现。
/// </para>
/// <para><b>三条必须守住的约束（前两条本仓通用纪律，第三条是本操作专属）：</b></para>
/// <list type="number">
/// <item><b>Validate 阶段不碰 Provider</b>：审批之前不触碰 Azure。
/// 前置校验放在 Execute 开头（审批通过之后）—— 与电源操作同一纪律，由既有测试守护。</item>
/// <item><b>不信任载荷里的资源 ID。</b> 载荷随待审批请求<b>落盘</b>（<c>jobs.json</c>），
/// 照它去删等于「改一个持久化文件里的字符串就能删掉任意资源」。
/// 执行阶段必须重新读<b>真实挂载关系</b>并与请求求交集，只删交集。</item>
/// <item><b>审批不可绕过。</b> 删除不可恢复，因此 <c>CannotBypass = true</c> ——
/// 这是引擎里<b>唯一</b>能压过用户设置的机制，保证即使用户把审批档调成「关闭」也照样拦。
/// <see cref="ImpactAssessment.CannotBypass"/> 的注释写明了这类保证「必须由引擎兜底，
/// 不能靠每个调用点记得传 PreApproved = false」。</item>
/// </list>
/// </remarks>
public sealed class DeleteVmHandler(
    IVmDeleteExecutor executor,
    ILogger<DeleteVmHandler> logger) : IOperationHandler
{
    /// <summary>
    /// 载荷里「要连带删除哪些类别」的键，值为逗号分隔的 <see cref="VmLinkedResourceKind"/> 名。
    /// 缺失或为空 = 全部不连带（默认）。
    /// </summary>
    public const string PayloadLinkedKinds = "linkedKinds";

    public string OperationType => ComputeModule.OperationDelete;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        // 只做不依赖 Provider 的结构校验；真实存在性与挂载关系见 ExecuteAsync 开头。
        foreach (var part in PayloadParts(request))
        {
            if (!Enum.TryParse<VmLinkedResourceKind>(part, ignoreCase: true, out _))
            {
                throw new OperationValidationException($"未知的连带删除类别：{part}。");
            }
        }

        return Task.CompletedTask;
    }

    public async Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var name = VmPowerHandlerBase.VmDisplayName(request);
        var requested = RequestedKinds(request);

        // 影响分析允许读 Provider —— 既有先例：共享子网 NSG 的影响面就是在 Handler 里现查的
        // （NetworkRuleHandlers.SharedNsgImpactAsync）。这里同理，数量必须来自真实挂载关系。
        var linked = await executor.GetLinkedResourcesAsync(request, ct).ConfigureAwait(false);
        var selected = linked.Where(item => requested.Contains(item.Kind)).ToList();
        var kept = linked.Where(item => !requested.Contains(item.Kind)).ToList();

        var description = new StringBuilder();
        description.Append($"删除虚拟机 {name}，此操作不可恢复。");

        if (selected.Count > 0)
        {
            description.Append($"将一并删除：{string.Join("、", selected.Select(item => item.DisplayName))}。");
        }

        if (kept.Count > 0)
        {
            // 「以为删干净了、其实还在扣钱」是这类操作最典型的事故，
            // 必须显式说出来而不是等用户自己去发现。
            description.Append($"以下资源会保留并继续计费：{string.Join("、", kept.Select(item => item.DisplayName))}。");
        }

        return new ImpactAssessment
        {
            RequiresApproval = true,
            CannotBypass = true,
            AffectedResources = 1 + selected.Count,
            Description = description.ToString()
        };
    }

    public Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct) =>
        ExecuteAsync(request, static (_, _) => Task.CompletedTask, ct);

    public async Task<string?> ExecuteAsync(
        OperationRequest request, Func<string, CancellationToken, Task> reportProgress, CancellationToken ct)
    {
        // ① 存在性以真实读回为准 —— 载荷说"它存在"不算数
        if (!await executor.VmExistsAsync(request, ct).ConfigureAwait(false))
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到虚拟机：{request.ResourceId}");
        }

        // ② 重新读真实挂载关系并与请求求交集：
        //    载荷只表达「用户想删什么」，不表达「允许删什么」——
        //    它随待审批请求落盘，照它去删等于「改一个文件字符串就能删任意资源」。
        var requested = RequestedKinds(request);
        var linked = await executor.GetLinkedResourcesAsync(request, ct).ConfigureAwait(false);
        var toDelete = linked.Where(item => requested.Contains(item.Kind)).ToList();

        // ③ 先删 VM —— **顺序不可颠倒**：NIC 与磁盘附着在 VM 上时无法删除。
        //    顺序放在这里而不是执行器里，是为了让它成为一条可断言的不变量。
        var requestId = await executor.DeleteVmAsync(request, ct).ConfigureAwait(false);

        logger.LogInformation("执行 {Operation}：{ResourceId}，连带删除 {Count} 项",
            OperationType, request.ResourceId, toDelete.Count);

        await reportProgress(
            toDelete.Count > 0 ? "虚拟机已删除，正在清理关联资源…" : "虚拟机已删除。", ct).ConfigureAwait(false);

        // ④ 再删连带资源——逐个尝试，某一件失败不能让后面的也没机会删（曾经的真实 bug：
        //    执行器只捕获 RequestFailedException，别的异常会直接冒出这个循环，
        //    让排在它后面、本该删的资源全部落空）。
        var failed = new List<string>();
        foreach (var resource in toDelete)
        {
            var (success, reason) = await executor.DeleteLinkedAsync(request, resource, ct).ConfigureAwait(false);
            if (success)
            {
                await reportProgress($"已删除 {resource.DisplayName}", ct).ConfigureAwait(false);
            }
            else
            {
                var text = string.IsNullOrWhiteSpace(reason)
                    ? resource.DisplayName
                    : $"{resource.DisplayName}（{reason}）";
                failed.Add(text);
                await reportProgress($"清理 {text} 失败", ct).ConfigureAwait(false);
            }
        }

        if (failed.Count > 0)
        {
            // 虚拟机已删，但这次操作**没有达成它的全部目标**，因此如实报失败而不是报成功。
            // 消息里必须说清"已经删了什么、还剩什么"，否则用户会以为整个操作都没生效；
            // 更不能静默吞掉 —— 那些资源还在计费。
            throw new CloudFlowException(CloudFlowErrorCode.Unknown,
                $"虚拟机已删除，但以下资源未能删除，它们仍然存在并继续计费：{string.Join("、", failed)}");
        }

        return requestId;
    }

    /// <summary>
    /// Verify：确认虚拟机<b>已消失</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 删除的 Verify 语义与电源操作的「状态等于某值」不同，是「资源已消失」。
    /// ARM 调用统一用 <c>WaitUntil.Completed</c>，LRO 在 Execute 内已完成，
    /// 因此单次 404 判定即可 —— 全仓没有任何轮询循环，这里也不引入。
    /// </para>
    /// <para>
    /// <b>这里刻意不校验连带资源。</b> 它们删不删得掉已经由 Execute 的返回值决定了：
    /// 任何一件没删成功都会让 Execute 抛错，Job 直接 Failed，<b>根本轮不到 Verify</b>。
    /// 再读一次挂载关系既做不到（VM 都没了，读不出它的挂载），也会与 Execute 的结论重复。
    /// </para>
    /// </remarks>
    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct) =>
        !await executor.VmExistsAsync(request, ct).ConfigureAwait(false);

    /// <summary>载荷里声明的连带删除类别集合。</summary>
    private static HashSet<VmLinkedResourceKind> RequestedKinds(OperationRequest request) =>
        [.. PayloadParts(request)
            .Select(part => Enum.TryParse<VmLinkedResourceKind>(part, ignoreCase: true, out var kind) ? kind : (VmLinkedResourceKind?)null)
            .Where(kind => kind is not null)
            .Select(kind => kind!.Value)];

    private static IEnumerable<string> PayloadParts(OperationRequest request) =>
        !request.Payload.TryGetValue(PayloadLinkedKinds, out var raw) || string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
