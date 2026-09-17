using CloudFlow.Core.Errors;
using CloudFlow.Core.Operations;
using CloudFlow.Modules.Compute.Models;
using CloudFlow.Modules.Compute.Services;
using CloudFlow.Operations.Pipeline;
using Microsoft.Extensions.Logging;

namespace CloudFlow.Modules.Compute.Operations;

/// <summary>
/// VM 电源操作处理器基类：Start / Restart / Power Off / Deallocate。
///
/// 与 Provider 无关 —— 真实 ARM 还是 Demo 内存数据面由 <see cref="IVmPowerExecutor"/> 的实现决定，
/// Handler 只表达"这个操作是什么、什么状态才允许做、做完应该是什么状态"。
///
/// **Validate 阶段刻意不做任何 Provider I/O**：审批之前不触碰 Azure。
/// 前置状态校验放在 Execute 开头（审批通过之后），这条不变量由
/// AzureRealVmRestartHandlerTests 守护。
/// </summary>
public abstract class VmPowerHandlerBase : IOperationHandler
{
    private readonly string _operationType;
    private readonly VmPowerAction _action;
    private readonly IVmPowerExecutor _executor;
    private readonly ILogger _logger;

    protected VmPowerHandlerBase(
        string operationType,
        VmPowerAction action,
        IVmPowerExecutor executor,
        ILogger logger)
    {
        _operationType = operationType;
        _action = action;
        _executor = executor;
        _logger = logger;
    }

    public string OperationType => _operationType;

    /// <summary>该操作要求的前置电源状态（null 表示由子类在 <see cref="ValidateState"/> 自定义）。</summary>
    protected virtual VmPowerState? RequiredStateBefore => null;

    /// <summary>动作完成后的期望状态；Verify 据此判定，不依赖 Payload。</summary>
    protected abstract VmPowerState StateAfter { get; }

    /// <summary>影响分析描述（§25）。</summary>
    protected abstract string Describe(OperationRequest request);

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        // 只做不依赖 Provider 的结构校验；状态校验见 ExecuteAsync 开头的说明。
        _ = request.ResourceId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 破坏性电源操作默认要求审批（§25）。Start 是恢复性操作，子类覆写为无需审批。
    /// </summary>
    public virtual Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(new ImpactAssessment
        {
            RequiresApproval = true,
            AffectedResources = 1,
            Description = Describe(request)
        });

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var current = await _executor.GetPowerStateAsync(request, ct).ConfigureAwait(false);
        if (current is null)
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到虚拟机：{request.ResourceId}");
        }

        ValidateState(request, current.Value);

        _logger.LogInformation("执行 {Operation}：{ResourceId}（当前 {State}）",
            _operationType, request.ResourceId, current.Value);

        return await _executor.ExecuteAsync(request, _action, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var state = await _executor.GetPowerStateAsync(request, ct).ConfigureAwait(false);
        return state == StateAfter;
    }

    /// <summary>状态校验（子类可覆写，如 Start 允许 Stopped / Deallocated）。</summary>
    protected virtual void ValidateState(OperationRequest request, VmPowerState current)
    {
        if (RequiredStateBefore is { } required && current != required)
        {
            throw new OperationValidationException(
                $"虚拟机“{VmDisplayName(request)}”当前为 {StateText(current)}，" +
                $"无法执行 {_operationType}（要求 {StateText(required)}）。");
        }
    }

    /// <summary>Resource ID 末段即 VM 名称（Azure Resource ID 是唯一主键）。</summary>
    public static string VmDisplayName(OperationRequest request)
    {
        var id = request.ResourceId;
        var slash = id.LastIndexOf('/');
        return slash >= 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }

    /// <summary>中文状态文本（模块层不依赖 UI 转换器，用轻量映射）。</summary>
    protected static string StateText(VmPowerState state) => state switch
    {
        VmPowerState.Running => "运行中",
        VmPowerState.Stopped => "已停止",
        VmPowerState.Deallocated => "已解除分配",
        _ => state.ToString()
    };
}

/// <summary>vm.start：Stopped / Deallocated → Running，非破坏性操作，不需审批。</summary>
public sealed class StartVmHandler(IVmPowerExecutor executor, ILogger<StartVmHandler> logger)
    : VmPowerHandlerBase(ComputeModule.OperationStart, VmPowerAction.Start, executor, logger)
{
    protected override VmPowerState StateAfter => VmPowerState.Running;

    /// <summary>启动是恢复性操作，不打断任何服务。</summary>
    protected override string Describe(OperationRequest request) =>
        $"启动虚拟机 {VmDisplayName(request)}，恢复其运行状态。";

    public override Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(ImpactAssessment.None);

    protected override void ValidateState(OperationRequest request, VmPowerState current)
    {
        if (current == VmPowerState.Running)
        {
            throw new OperationValidationException(
                $"虚拟机“{VmDisplayName(request)}”已在运行中，无需启动。");
        }
    }
}

/// <summary>vm.restart：Running → Running（重启会短暂中断服务，必须审批）。</summary>
public sealed class RestartVmHandler(IVmPowerExecutor executor, ILogger<RestartVmHandler> logger)
    : VmPowerHandlerBase(ComputeModule.OperationRestart, VmPowerAction.Restart, executor, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;

    protected override VmPowerState StateAfter => VmPowerState.Running;

    protected override string Describe(OperationRequest request) =>
        $"重启将短暂中断虚拟机 {VmDisplayName(request)} 上的服务，完成后自动恢复运行。";
}

/// <summary>vm.power_off：Running → Stopped，计算资源仍保留，费用可能继续。</summary>
public sealed class PowerOffVmHandler(IVmPowerExecutor executor, ILogger<PowerOffVmHandler> logger)
    : VmPowerHandlerBase(ComputeModule.OperationPowerOff, VmPowerAction.PowerOff, executor, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;

    protected override VmPowerState StateAfter => VmPowerState.Stopped;

    protected override string Describe(OperationRequest request) =>
        $"关机将停止虚拟机 {VmDisplayName(request)}，计算资源仍保留分配，费用可能继续产生。";
}

/// <summary>vm.deallocate：Running → Deallocated，释放计算资源并停止计算计费。</summary>
public sealed class DeallocateVmHandler(IVmPowerExecutor executor, ILogger<DeallocateVmHandler> logger)
    : VmPowerHandlerBase(ComputeModule.OperationDeallocate, VmPowerAction.Deallocate, executor, logger)
{
    protected override VmPowerState? RequiredStateBefore => VmPowerState.Running;

    protected override VmPowerState StateAfter => VmPowerState.Deallocated;

    protected override string Describe(OperationRequest request) =>
        $"解除分配将释放虚拟机 {VmDisplayName(request)} 的计算资源并停止计算计费" +
        "（磁盘与保留 IP 可能继续计费）。";
}

/// <summary>
/// vm.resize：更改 VM 规格（设计文档 §19）。UI 对话框确认后 PreApproved 提交。
/// 规格变更会重启虚拟机，且可能改变计费口径，因此同样要求审批。
/// </summary>
public sealed class ResizeVmHandler(
    IVmPowerExecutor executor,
    ILogger<ResizeVmHandler> logger) : IOperationHandler
{
    /// <summary>
    /// 演示/常用规格清单（UI 下拉）。真实 Azure 的可用规格随区域变化，
    /// 由 IVmSizeCatalog 提供；此处保留一份稳定的常用集合供对话框展示。
    /// </summary>
    public static readonly string[] SupportedSizes =
    [
        "Standard_B2s",
        "Standard_B2ms",
        "Standard_B4ms",
        "Standard_D2s_v5",
        "Standard_D4s_v5",
        "Standard_E4s_v5"
    ];

    public string OperationType => ComputeModule.OperationResize;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        if (!request.Payload.TryGetValue("newSize", out var newSize) || string.IsNullOrWhiteSpace(newSize))
        {
            throw new OperationValidationException("缺少必需参数：newSize。");
        }

        return Task.CompletedTask;
    }

    public Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct) =>
        Task.FromResult(new ImpactAssessment
        {
            RequiresApproval = true,
            AffectedResources = 1,
            Description =
                $"更改规格会重启虚拟机 {VmPowerHandlerBase.VmDisplayName(request)}，" +
                $"目标规格 {request.Payload.GetValueOrDefault("newSize")}，计费口径可能随之变化。"
        });

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        var newSize = request.Payload["newSize"];

        var currentSize = await executor.GetVmSizeAsync(request, ct).ConfigureAwait(false);
        if (currentSize is null)
        {
            throw new CloudFlowException(CloudFlowErrorCode.ResourceNotFound,
                $"未找到虚拟机：{request.ResourceId}");
        }

        if (string.Equals(currentSize, newSize, StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationValidationException($"目标规格与当前规格相同（{newSize}）。");
        }

        logger.LogInformation("执行 {Operation}：{ResourceId} {From} → {To}",
            OperationType, request.ResourceId, currentSize, newSize);

        return await executor.ResizeAsync(request, newSize, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        var size = await executor.GetVmSizeAsync(request, ct).ConfigureAwait(false);
        return string.Equals(size, request.Payload["newSize"], StringComparison.OrdinalIgnoreCase);
    }
}
