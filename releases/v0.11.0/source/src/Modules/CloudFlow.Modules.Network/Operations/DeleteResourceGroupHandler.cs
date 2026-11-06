using System.Text;
using System.Text.RegularExpressions;
using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Network.Models;
using CloudFlow.Modules.Network.Services;
using CloudFlow.Operations.Pipeline;

namespace CloudFlow.Modules.Network.Operations;

/// <summary>
/// resourcegroup.delete：删除资源组（设计文档 v3.2 §"资源"页——清理创建虚拟机流程按需
/// 新建、但删除虚拟机时刻意不连带删除的网络类残留：虚拟网络/子网/资源组本身）。
/// </summary>
/// <remarks>
/// <para>
/// <b>删除是级联的</b>：组内不管有什么都会被删掉，影响面不像删虚拟机那样可以枚举固定几类，
/// 必须在 Impact 阶段现查组内实际资源（<see cref="IResourceGroupDeleteExecutor.GetContainedResourcesAsync"/>）
/// 摆给用户看，命中虚拟机类型时额外点名——防止用户把"清理网络残留"和"删掉还在用的机器"混为一谈。
/// </para>
/// <para>
/// <b>审批不可绕过</b>（<c>CannotBypass = true</c>）：跟删除虚拟机同一条纪律，这个操作的
/// 破坏半径比删一台虚拟机更大，没有理由审批门槛更低。
/// </para>
/// </remarks>
public sealed partial class DeleteResourceGroupHandler(
    IResourceGroupDeleteExecutor executor) : IOperationHandler
{
    /// <summary>Azure 资源组名字符集，与 CreateVmHandler 的同名校验保持一致。</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9._\-()]+$", RegexOptions.Compiled)]
    private static partial Regex ResourceGroupNameCharsetRegex();

    public string OperationType => ResourceGroupModule.OperationDelete;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        var rgName = ParseResourceGroupName(request.ResourceId);
        if (string.IsNullOrWhiteSpace(rgName))
        {
            throw new OperationValidationException($"无法从目标解析资源组：{request.ResourceId}。");
        }

        if (rgName.Length is < 1 or > 90 || rgName.EndsWith('.') ||
            !ResourceGroupNameCharsetRegex().IsMatch(rgName))
        {
            throw new OperationValidationException($"资源组名称「{rgName}」不合法。");
        }

        return Task.CompletedTask;
    }

    public async Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        var rgName = ParseResourceGroupName(request.ResourceId) ?? request.ResourceId;

        // Impact 阶段允许查 Provider——既有先例：删除虚拟机的连带资源数量也是这么来的。
        var contained = await executor.GetContainedResourcesAsync(request, ct).ConfigureAwait(false);
        var vmNames = contained.Where(r => r.IsVirtualMachine).Select(r => r.Name).ToList();

        var description = new StringBuilder();
        description.Append($"删除资源组 {rgName}，此操作不可恢复。");

        if (contained.Count > 0)
        {
            description.Append($"组内共有 {contained.Count} 个资源，将全部一并删除。");
        }
        else
        {
            description.Append("该资源组目前为空。");
        }

        if (vmNames.Count > 0)
        {
            // 最容易被忽略的事故：以为只是清理网络残留，实际上一并删掉了还在用的虚拟机。
            description.Append($" 其中包含虚拟机：{string.Join("、", vmNames)}，将一并删除。");
        }

        return new ImpactAssessment
        {
            RequiresApproval = true,
            CannotBypass = true,
            AffectedResources = contained.Count,
            Description = description.ToString()
        };
    }

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        // 存在性以真实读回为准：Impact 分析之后、真正执行之前的这段时间里，目标可能已经被
        // 别处删掉（例如用户重复点击、或者是一个此前因为应用崩溃而没能正常收尾的旧待审批
        // 任务）。这种情况下"资源组已经不存在"正是这个操作想要的结果，直接视为已达成，
        // 不必真的再调一次 Delete 去撞一个 404——真实报过的 Bug：这个 404 会带着完整的
        // HTTP 诊断转储原样冒给用户，而不是一句"已经删除"。
        if (!await executor.ExistsAsync(request, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await executor.DeleteAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Verify：确认资源组已消失（级联删除完成的标志——组内资源无法单独确认，
    /// 资源组本身消失就意味着 Azure 侧已经把里面的东西都清完了）。</summary>
    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct) =>
        !await executor.ExistsAsync(request, ct).ConfigureAwait(false);

    private static string? ParseResourceGroupName(string resourceId)
    {
        var segments = resourceId.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }
}
