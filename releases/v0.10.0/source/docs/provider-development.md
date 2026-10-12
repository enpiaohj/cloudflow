# API / Provider 开发规范

如何在 CloudFlow 里新增一种资源类型或一种写操作。先读
[`architecture.md`](architecture.md#operation-engine写操作的唯一入口) 了解 Operation Engine
的整体流水线；本篇是面向"要动手写代码"的具体规范与worked example。

## 核心规则

**任何会改变 Azure 状态的操作都必须实现 `IOperationHandler` 并经 Operation Engine 提交**，
禁止 ViewModel 或任何界面代码直接调用 `Azure.ResourceManager.*` 的写方法（`CreateOrUpdateAsync`、
`DeleteAsync` 等）。只读查询（清单、详情）不受此限制，可以直接调用 ARM SDK 或 Resource Graph。

## 契约总览

| 接口 | 定义位置 | 职责 |
| --- | --- | --- |
| `IResourceModule` | `CloudFlow.Core.Resources` | 声明一个模块覆盖哪些 Azure 资源类型、暴露哪些操作 |
| `IOperationHandler` | `CloudFlow.Operations.Pipeline` | 一种操作在流水线各阶段的具体逻辑 |
| `I<Xxx>Executor`（自定义） | 各 `Modules.*` 项目 | Handler 与"真正怎么操作 Azure"之间的抽象，
  分真实（ARM）与 Mock（Demo 模式）两套实现 |

## 新增一种操作的步骤

以下按"新增一个删除类操作"的真实模式说明（可参考已有实现
[`DeleteResourceGroupHandler`](../src/Modules/CloudFlow.Modules.Network/Operations/DeleteResourceGroupHandler.cs)）。

### 1. 声明模块元数据

在对应 `Modules.*` 项目下新增（或复用已有）`IResourceModule` 实现，声明这个操作的
`OperationType` 常量（约定为 `<资源>.<动作>`，全小写，如 `vm.restart`、`resourcegroup.delete`）：

```csharp
public sealed class MyResourceModule : IResourceModule
{
    public const string OperationDoSomething = "myresource.dosomething";

    public string ModuleId => "myresource";
    public IReadOnlyList<string> ResourceTypes => ["Microsoft.SomeProvider/someType"];
    public IReadOnlyList<string> Commands => [OperationDoSomething];
}
```

### 2. 定义 Executor 契约

Handler 不直接认识 Azure SDK 或 Demo 数据，通过一个自定义接口与"真正怎么做"解耦：

```csharp
public interface IMyResourceExecutor
{
    Task<string?> DoSomethingAsync(OperationRequest request, CancellationToken ct = default);
    // Impact 阶段需要读取的信息也放在这里，比如受影响资源数、当前状态等。
}
```

分别提供：

- **真实实现**：放在 `CloudFlow.Azure` 项目对应子目录，调用 `Azure.ResourceManager.*` SDK。
- **Mock 实现**：放在同一个 `Modules.*` 项目内，操作内存态的演示数据，供 Demo 模式使用。
- **Router**：放在 `src/CloudFlow.App/Infrastructure/`，按当前账户的 `ProviderType`
  （真实账户 / Demo）路由到上述两者之一，是 DI 容器里真正注册的实现（参考已有的
  `ResourceGroupDeleteExecutorRouter`）。

### 3. 实现 Handler

```csharp
public sealed class MyOperationHandler(IMyResourceExecutor executor) : IOperationHandler
{
    public string OperationType => MyResourceModule.OperationDoSomething;

    public Task ValidateAsync(OperationRequest request, CancellationToken ct)
    {
        // 校验请求本身合法（格式、必填字段）。不合法抛 OperationValidationException。
        return Task.CompletedTask;
    }

    public async Task<ImpactAssessment> AnalyzeImpactAsync(OperationRequest request, CancellationToken ct)
    {
        // 如实评估这次操作会影响什么。允许在这一步向 Executor 发起只读查询
        // （已有先例：删除资源组在这一步真的去查了组内有哪些资源）。
        return new ImpactAssessment
        {
            RequiresApproval = true,          // 破坏性操作应要求审批
            CannotBypass = false,             // 高风险操作（如级联删除）设为 true，
                                               // 即使审批策略被关掉也必须弹确认
            Description = "这次操作会做什么，用人能看懂的一句话描述",
            AffectedResources = 1
        };
    }

    public async Task<string?> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        return await executor.DoSomethingAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(OperationRequest request, string? requestId, CancellationToken ct)
    {
        // 执行完成不等于操作成功——回读实际状态确认真的达到了预期效果。
        // 没有可回读的场景（比如目标已确认不存在）时才允许直接返回 true。
        return true;
    }
}
```

需要在 Execute 内部上报多个子步骤进度（如"创建虚拟机"要经过资源组 → 网络 → 网卡 → 虚拟机好几步）
时，覆写带 `reportProgress` 回调的 `ExecuteAsync` 重载；绝大多数只有一次 ARM 调用的操作不需要覆写，
默认实现会自动转发。

### 4. 注册到 DI 容器

在 `src/CloudFlow.App/App.xaml.cs` 里补上（真实执行器、Mock 执行器、Router、Handler 四行，
参考文件里已有的同类注册）：

```csharp
services.AddSingleton<MockMyResourceExecutor>();
services.AddSingleton<ArmMyResourceExecutor>();
services.AddSingleton<IMyResourceExecutor, MyResourceExecutorRouter>();
services.AddTransient<IOperationHandler, MyOperationHandler>();
```

### 5. 提交入口

界面代码不直接构造 `OperationRequest`，通过一层 Service（如已有的 `IResourceGroupService`）
封装成一个有意义的方法名：

```csharp
public sealed class MyResourceService(
    IOperationEngine engine, OperationRequestFactory requests, IApprovalPolicy approvalPolicy)
    : IMyResourceService
{
    public Task<OperationJob> DoSomethingAsync(string subscriptionId, string resourceId, CancellationToken ct = default) =>
        engine.SubmitAsync(requests.Create(
            MyResourceModule.OperationDoSomething, subscriptionId, resourceId,
            "对 xxx 执行 yyy",
            risk: RiskLevel.Medium,
            preApproved: approvalPolicy.ShouldAutoApprove(RiskLevel.Medium)), ct);
}
```

ViewModel 调用这个 Service，拿到 `OperationJob`；如果状态是 `WaitingApproval`，弹
`ImpactApprovalDialog` 让用户确认，确认后调用 `IOperationEngine.ApproveAsync(jobId)` 真正执行。

## 测试约定

- **Handler 单测**放在 `tests/CloudFlow.Operation.Tests/`，用一个假的 Executor（实现同一个接口，
  行为可控）验证 Validate / Impact / Verify 的逻辑分支，不需要真实 Azure 也不需要启动 UI。
- **真实 ARM 实现**如需要针对真实账户验证，放在 `tests/CloudFlow.Azure.IntegrationTests/`，
  标注 `[Trait("Category", "Integration")]`；这类测试只应操作用户明确用于测试的资源，不得触碰
  生产资源。
- 新增 Handler 后至少覆盖：一次正常执行、Impact 阶段的关键判断分支（如"影响资源数为 0 时不同的
  文案"）、Validate 拒绝非法输入的情形。

## 命名与文件位置约定

| 内容 | 位置 |
| --- | --- |
| `IResourceModule` 实现、Executor 接口、Handler | `src/Modules/CloudFlow.Modules.<模块名>/` |
| Mock Executor | 与接口同一个 `Modules.*` 项目 |
| 真实 Executor（ARM） | `src/CloudFlow.Azure/<资源类别>/` |
| Router | `src/CloudFlow.App/Infrastructure/` |
| 面向 ViewModel 的 Service 封装 | 与 Executor 接口同一个 `Modules.*` 项目 |
