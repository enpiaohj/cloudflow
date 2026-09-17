# CloudFlow 项目规则

## Repository Rule

- **产品名**：CloudFlow（副标题 CloudFlow for Microsoft Azure）
- **仓库名**：`cloudflow`（GitHub，lowercase-kebab-case，默认 Private）
- **默认分支**：`main`
- **可见性**：Private（转 Public 需用户明确决定）
- **版本**：Semantic Versioning，当前 `0.4.0`（Tag `v0.4.0`；已发布 `v0.1.0`、`v0.2.0`、`v0.3.0`、`v0.4.0`）
- **Commit**：Conventional Commits（`feat:` / `fix:` / `docs:` / `refactor:` / `test:` / `build:` / `ci:` / `chore:` / `release:`）
- **Artifact 策略**：`releases/vX.Y.Z/` 快照中 `source/` 与 `CHANGELOG.md` 入库，二进制产物不入库（交付走 GitHub Releases）

## 产品基准（重要）

- 产品设计唯一基准：`docs/01-产品设计/20260913-CloudFlow 云资源智能运维平台产品设计文档 v3.2.md`（架构冲突时以该文档为准）
- UI 布局与界面以 `docs/01-产品设计/UI/UI概念图1.png`、`UI概念图2.png` 为准；**顶栏第二选择器命名用 "Scope"（文档 §8）**
- **范围变更走「另存新文件 + 升版号」并在文档「修订记录」登记**（不要就地改范围）。当前 P1 已含 **Create VM / Delete VM**（v3.1 §87，v3.2 修订）—— v3.0 时代那句「P1 不做 Create VM」**已作废**；创建虚拟机时资源组 / 虚拟网络 / 子网不存在会按需新建、存在则复用（v3.2 §87.2），见 `…产品设计文档 v3.2.md` 的修订记录
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
- 阶段路线见设计文档 §78：P0 Spike → P1（VM Ops + 多订阅）→ P1.5（多账号 UI）→ …
- P1 Exit Gate 清单见设计文档 §80，阶段完成与否以清单为准，不允许"页面完成 = 阶段完成"
