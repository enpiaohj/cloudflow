# CloudFlow 架构说明

本篇介绍 CloudFlow 的整体架构、核心设计原则与模块划分，面向想理解代码库结构的贡献者与使用者。
它是公开的架构摘要，不是完整的产品设计规格——产品范围与路线图由维护者另行管理。

## 一句话概括

CloudFlow 是一个 WPF 桌面应用，用统一的 **Operation Engine** 把所有对 Azure 的写操作（启停虚拟机、
创建/删除资源等）纳入同一条流水线（校验 → 影响分析 → 权限判定 → 执行 → 校验结果 → 审计），
杜绝"界面按钮直接调 Azure SDK"这种绕过审计与确认的路径。

## 核心模型

```
Account → Tenant → Scope → Resource → Operation
```

- **Account**：一个登录身份（工作/学校账户走 MSAL，个人 Microsoft 账户走嵌入式 Azure CLI）。
- **Tenant**：账户所属的 Entra 租户。
- **Scope**：当前操作作用的范围——可以是单个订阅、多个订阅，也可以是"当前账户全部可访问订阅"。
  顶栏第二个选择器就是 Scope，刻意不叫 "Subscription"，因为一个 Scope 可以横跨多个订阅。
- **Resource**：一件具体的 Azure 资源，唯一主键是它的 **Resource ID**（不是名称——同名资源可能
  分布在不同资源组/订阅）。
- **Operation**：一次会改变 Azure 状态的写操作，必须经过 Operation Engine。

所有模块的查询方法统一接受 `ResourceScope`，禁止 `GetVirtualMachines(subscriptionId)` 这种绑死单
订阅的方法签名——这样同一套查询逻辑天然支持"只看一个订阅"和"看全部可访问订阅"两种场景。

## Operation Engine：写操作的唯一入口

```
Validate → Impact Analysis → Permission → Execute → Verify → Audit
```

| 阶段 | 做什么 |
| --- | --- |
| Validate | 校验请求本身合法（如资源名格式、必填字段） |
| Impact Analysis | 如实评估这个操作会影响什么、影响多大（如"删除资源组会连带删除组内 12 个资源"） |
| Permission | 按当前审批策略判定是否需要人工确认；高风险操作（如删除资源组）标记为 `CannotBypass`，
  即使把审批档位关掉也必须弹确认 |
| Execute | 真正调用 Azure（ARM API），或在 Demo 模式下调用内存态的 Mock 实现 |
| Verify | 执行完成后回读实际状态，确认操作真的达到了预期效果——不是"调用没报错"就算完成 |
| Audit | 写入本地审计日志，包含谁、何时、对哪个 Resource ID、做了什么 |

任何界面代码都不允许绕开这条流水线直接调用 Azure SDK。新增一种写操作时，需要实现
[`IOperationHandler`](../src/CloudFlow.Operations/Pipeline/IOperationHandler.cs)，具体做法见
[`provider-development.md`](provider-development.md)。

## 数据来源的两条路径

- **Inventory / 发现**：用 Azure Resource Graph（跨订阅、跨资源类型的统一查询接口），用于列表页
  这种"看很多资源"的场景。
- **详情 / 写操作**：用 ARM API（`Azure.ResourceManager.*` SDK），用于单个资源的完整详情和实际变更。

这两条路径不能混用：Resource Graph 的数据有缓存延迟，不能拿它的结果直接做写操作判断（比如"资源是否
存在"必须用 ARM 实时读回，不能信 Resource Graph 那份可能过期的快照）。

## 身份与账户

CloudFlow 支持两类账户，统一通过 `ICloudIdentityProvider` 抽象、由 `CloudAccountDirectory` 按
`ProviderType` 路由：

- **工作或学校账户**：MSAL.NET，交互式登录走系统浏览器（Public Client，无 Client Secret）。
- **个人 Microsoft 账户**：嵌入式 Azure CLI（设备码流程）。个人账户不支持 MSAL 交互式登录的 WAM
  Broker 路径在部分网络环境下不可用，改用 Azure CLI 的设备码登录，Token 与账户状态经进程级
  `AZURE_CONFIG_DIR` 与其它账户完全隔离，互不干扰、互不共享。这份 Runtime 由 CloudFlow 在用户
  第一次添加个人账户时按需下载（详见 [`authentication.md`](authentication.md)），不随发布的 EXE
  一起分发。CLI 失败经 `AzureCliFailure` 归因：幂等读操作（取令牌 / 列订阅）对网络瞬断自动重试，
  失败措辞收敛为可行动的提示；启动时网络瞬断不移除账户登记，仅账户级失效才清除。

两类账户可以同时登录多个，互相切换不会串台。

## 模块与代码组织

```
src/
  CloudFlow.Core          # 平台核心模型：Identity / Scopes / Resources / Operations / Errors
  CloudFlow.Data          # 本地持久化（Saved Scopes、审计日志、应用设置等，纯文件存储）
  CloudFlow.Operations    # Operation Engine 本体（流水线调度、Job 状态机）
  CloudFlow.Azure         # Azure 适配层：MSAL / 嵌入式 CLI 认证、ARM、Resource Graph
  CloudFlow.Terminal      # SSH 会话与凭据库（DPAPI 保险库、主机指纹校验、xterm.js 终端控件）
  Modules/
    CloudFlow.Modules.Compute   # 计算资源模块（虚拟机：清单、详情、电源、创建、删除、Resize）
    CloudFlow.Modules.Network   # 网络与通用资源模块（NSG 规则、资源组、任意资源删除）
  CloudFlow.App           # WPF 桌面应用（MVVM，CommunityToolkit.Mvvm）
tests/                    # 按项目拆分的单元测试与 Azure 集成测试
tools/CloudFlow.Spike      # 独立于主应用的技术验证控制台（不随发布产物分发）
```

**Modules** 是资源类型维度的划分（计算 / 网络……），每个模块内部再分只读 Service（清单/详情查询）
与写操作 Handler + Executor（Operation Engine 的执行体）。真实实现在 `CloudFlow.Azure`，Demo 模式
用的 Mock 实现在各自的 `Modules.*` 项目内，通过依赖注入切换（见 `App.xaml.cs`），两套实现共用同一个
接口，界面代码完全不感知当前是真实数据还是演示数据。

## Demo 模式

未登录任何账户时，CloudFlow 不是"什么都不能用"，而是切换到 Demo 模式：所有清单、详情、成本等数据
来自内存态的 `Mock*Service`，可以完整体验创建/删除虚拟机、资源清理等全部界面流程而不接触真实 Azure
账户。已登录后如果某次真实查询失败，会如实报错，不会静默回退成演示数据——这两种状态必须能被用户
明确分辨。

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | WPF + WPF-UI（Fluent Design）+ CommunityToolkit.Mvvm，.NET 8 |
| 身份认证 | MSAL.NET（工作/学校账户）+ 嵌入式 Azure CLI（个人账户，设备码流程） |
| 资源发现 | Azure Resource Graph |
| 写操作 | Azure.ResourceManager.*（ARM SDK），全部经 Operation Engine |
| 终端 | WebView2 + xterm.js（应用级底部面板，多标签 SSH 会话） |
| 本地存储 | 纯文件（JSON），无数据库依赖 |

## 延伸阅读

- [`user-guide.md`](user-guide.md) —— 面向使用者的操作手册
- [`development.md`](development.md) —— 面向贡献者的开发环境与流程
- [`provider-development.md`](provider-development.md) —— 如何新增一种资源/操作
- [`authentication.md`](authentication.md) —— 身份认证配置详解
