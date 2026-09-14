# CloudFlow 项目规则

## Repository Rule

- **产品名**：CloudFlow（副标题 CloudFlow for Microsoft Azure）
- **仓库名**：`cloudflow`（GitHub，lowercase-kebab-case，默认 Private）
- **默认分支**：`main`
- **可见性**：Private（转 Public 需用户明确决定）
- **License**：GPL-3.0（根目录 `LICENSE`）；随源码分发的第三方组件（xterm.js 等）见 `THIRD-PARTY-NOTICES.md`
- **版本**：Semantic Versioning，当前 `0.5.0`（Tag `v0.5.0`；已发布 `v0.1.0`、`v0.2.0`、`v0.3.0`、`v0.4.0`、`v0.4.1`、`v0.5.0`）
- **Commit**：Conventional Commits（`feat:` / `fix:` / `docs:` / `refactor:` / `test:` / `build:` / `ci:` / `chore:` / `release:`）
- **Artifact 策略**：`releases/vX.Y.Z/` 快照中 `source/` 与 `CHANGELOG.md` 入库，二进制产物不入库（交付走 GitHub Releases）

## 产品基准（重要）

- 产品设计过程文档（范围演进历史、内部技术验证记录、UI 视觉方案迭代）不随本仓库分发，
  由维护者存放在独立的私有工作区（`ai-coding-workspace/projects/cloudflow/`）管理，
  不在本仓库的 Git 历史中——公开架构说明见 `docs/architecture.md`。
- **架构冲突或范围变更，以维护者当时的私有设计文档 + 本文件的架构原则为准**；本文件是
  面向贡献者与 Claude Code 的公开摘要，不是完整设计规格。
- 顶栏第二选择器命名用 "Scope"（不叫 Subscription——一个 Scope 可以横跨多个订阅）
- 核心架构原则：
  - `Account → Tenant → Scope → Resource → Operation`
  - 所有模块查询统一接受 `ResourceScope`（禁止 `GetVirtualMachines(subscriptionId)` 式签名）
  - 所有 Write Operation 必须经 Operation Engine（Validate → Impact → Permission → Execute → Verify → Audit），**禁止 Button → Azure SDK 直连**
  - Inventory/发现用 Azure Resource Graph；详情/写操作用 ARM API
  - Azure Resource ID 是资源唯一主键；Job 必须保存认证上下文（AccountId + TenantId + ResourceId）

## 常用命令

```bash
dotnet build CloudFlow.sln          # 构建
dotnet test CloudFlow.sln           # 全部测试
dotnet run --project src/CloudFlow.App            # 运行桌面应用（当前 Demo/Mock 模式）
dotnet run --project tools/CloudFlow.Spike        # P0 技术验证（需先配置 appsettings.json）
```

- 仅改 Core/Operations 时先跑：`dotnet test tests/CloudFlow.Core.Tests tests/CloudFlow.Operation.Tests`

## 配置与安全

- `src/CloudFlow.App/appsettings.json` 与 `tools/CloudFlow.Spike/appsettings.json` 含真实 `ClientId`/`TenantId`，**已 gitignore，禁止提交**
- 模板：各项目 `appsettings.example.json`
- CloudFlow 不保存 Azure 密码；不提交任何 Secret

## 当前开发阶段约定

- **Demo 模式**：真实 Azure 接入前，UI 数据来自 `Mock*Service`（Modules.Compute / Modules.Network），注册在 `App.xaml.cs`。接入真实 Azure 时替换对应 DI 注册，Mock 服务保留用于 UI 开发与测试
- 详细阶段路线与 Exit Gate 清单见维护者私有工作区的设计文档，不在本仓库内追踪
