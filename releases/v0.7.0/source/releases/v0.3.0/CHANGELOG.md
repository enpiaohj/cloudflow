# CloudFlow v0.3.0

发布日期：2026-09-14

本版本新增「资源」管理入口（所有资源 / 资源组），用于清理创建虚拟机流程留下的资源；创建虚拟机支持按需新建资源组 / 虚拟网络 / 子网，并接入真实 Azure 区域、规格、资源组目录与预估月费；同时完成一轮界面专业化重设计与全站文案规范化。产品设计基准升级为 v3.2。

## Added

### 资源管理

- 侧栏新增「资源」分组（位于「任务」之上，默认折叠），含「所有资源」「资源组」两个子项。
- 「资源组」页：列出当前 Scope 下全部资源组（名称、区域、组内资源数），支持搜索。
- 「所有资源」页：列出全部资源（名称、类型图标、资源组、实际所在区域），支持搜索。
- 删除资源组（`resourcegroup.delete`）经 Operation Engine：Impact 阶段通过 Resource Graph 如实列出组内资源，并单独点名其中的虚拟机；`CannotBypass`，关闭审批档位也必须确认；须输入资源组名称（大小写敏感）才能继续，提供一键复制；Verify 以资源组不存在为准。
- 删除单个资源（`resource.delete`）经 Operation Engine，按 Resource ID 删除，同样不可绕过确认。
- 删除操作统一收进行尾「⋯」菜单，避免误点。
- Demo 模式提供对应的 Mock 执行器与演示数据。

### 创建虚拟机

- 资源组 / 虚拟网络 / 子网不存在时按需新建，已存在则复用且不改动其配置（设计文档 v3.2 §87.2）；仍不创建 NSG。
- 网络步骤由「已有子网 Resource ID」改为「虚拟网络 + 地址空间 + 子网名称 + 子网地址段」四个预填字段；虚拟网络默认命名 `{虚拟机名}-vnet`。
- 区域、规格、资源组下拉接入真实 Azure 目录：区域只保留可创建资源的物理区域，资源组翻页取全；向导先用推断清单立即弹出，真实数据返回后再替换。
- 选择已有资源组时自动带出其区域，避免 `InvalidResourceGroupLocation`。
- 规格默认优先支持 Gen2、核数 / 内存最小者；查询镜像 Hyper-V 世代，与规格世代不匹配时提前示警（仅提示，不拦截）。
- 基于 Azure 公开零售价目 API 显示预估月费（按 730 小时 / 月，仅计算费用，仅供参考）。
- 常用镜像扩充 Debian、RHEL、Windows 11；规格 / 镜像校验由白名单改为格式校验，可用性由 ARM 兜底。
- 资源组名称校验升级为 Azure 命名规则校验。

### 任务进度

- `OperationJob` 新增 `ProgressNote`，创建虚拟机按资源组 → 虚拟网络 → 网卡 → 虚拟机、删除虚拟机按虚拟机 → 连带资源上报子步骤进度。
- 顶栏新增「任务进行中」徽标，任意页面都能看到正在执行的任务及当前步骤。
- 创建 / 删除对话框在提交期间保持打开，就地显示进度与失败原因，失败后无需重填。

## Changed

- 界面专业化重设计：Segoe UI Variable 字体；卡片圆角 12 与分级阴影；按钮圆角 8 与按压态；统一的搜索框、信息条、加载骨架、空态（`EmptyState`）、复制按钮、面包屑样式。
- 首页改为 KPI 卡片与快捷操作网格；任务页、设置页、虚拟机列表 / 详情页与全部对话框统一视觉层级与间距。
- 全站提示与描述文案规范化；任务结果文案人性化（`JobPresentation`），区域显示中文名，磁盘类型 / 状态显示中文。
- Azure 错误提示从 ARM 错误信封中提取一句话，不再展示 `RequestFailedException` 的完整 HTTP 诊断转储。
- 统一确认对话框：关机 / 解除分配 / 删除规则 / 作废任务改用跟随主题的 `ConfirmDialog`，替换原生 `MessageBox`；统一各对话框取消 / 默认按钮的样式与键盘语义。
- VM 详情页：各 Tab 独立滚动、页头固定；默认标签为「摘要」；摘要按行数贪心分两栏；各 Tab 增加加载提示；「停止」更名为「关机」。
- 移除详情页磁盘 Tab 与首页中只会提示「尚未实现」的「创建快照」占位入口，创建快照统一走磁盘行菜单。
- 首页「创建虚拟机」快捷操作不再提示「不属于 P1 范围」，改为导航到虚拟机列表。
- 深色主题下主色按钮文字改为深色，对比度约由 2.8:1 提升到 6.8:1。
- 产品设计基准升级为 `20260913-CloudFlow 云资源智能运维平台产品设计文档 v3.2.md`。

## Fixed

- 删除虚拟机残留连带资源：某一件连带资源删除失败（含非 `RequestFailedException` 异常）会中断后续清理；现逐件尝试并汇总失败原因。
- 已停止（Stopped）的虚拟机无法直接解除分配；现仅在已解除分配时拒绝。
- `OperationEngine` 状态切换由同步阻塞改为真正 `await`；新增 `TaskScheduler.UnobservedTaskException` 全局兜底。
- Wpf.Ui 3.0.5 会把 U+FFFF 以上的图标码位截断为 16 位，导致设置页等处图标乱码；已替换受影响图标，并新增码位守护测试（XAML 字面量与 C# `SymbolRegular` 引用）。
- 侧栏分组展开箭头不翻转；vCPU 列头被截断；首页内容宽度塌缩到约 650px。
- 失败任务仍显示执行过程中的过期进度文案。
- 「所有资源」页区域错误显示为资源组所在区域，现显示资源自身区域。
- 详情页表格上鼠标滚轮失效。
- 创建 / 删除对话框进度回调在非 UI 线程触发时引发跨线程异常。

## Known Issues

- 删除资源组 / 删除单个资源在真实 Azure 上的端到端验证**未在本版本发布验证中执行**；已完成单元测试、静态测试与 Demo 模式界面验证。
- 批量 SSH 连接与批量主机指纹确认的真实 Azure 端到端验证仍**未执行**。
- 创建虚拟机的后台进度按操作类型匹配：同时创建多台虚拟机时，进度提示可能互相串台。
- 窗口 Mica 背景未启用（`CfThemeManager` 仍使用 `WindowBackdropType.None`）。
- 深色主题取值按 Fluent 深色惯例推导，暂无对应设计稿基准。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **459 通过，0 失败，0 跳过**。
  - App.Tests：32
  - Core.Tests：82
  - Operation.Tests：48
  - Terminal.Tests：116
  - Azure.IntegrationTests：181
- **Publish**：`dotnet publish src/CloudFlow.App/CloudFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:PublishDocumentationFiles=false`；发布目录仅含单个 EXE，不含 `appsettings.json`。
- **Standalone**：`CloudFlow-v0.3.0-win-x64.exe` 在未携带 `appsettings.json` 的目录启动，出现主窗口「CloudFlow」且响应正常，随后正常关闭（退出码 0）。
- **Version**：EXE `ProductVersion` = `0.3.0+96e406a4c4c1fc512e9ed32536cfc2b5770c3a75`，`FileVersion` = `0.3.0.0`。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Artifacts

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `CloudFlow-v0.3.0-win-x64.exe` | 223,417,965 字节 | `6ef21ebd11f01e7fe684e03af53c0a134e2d99b098f0cf367b79788c9d6a2e8f` |
| `CloudFlow-v0.3.0-win-x64.zip` | 86,120,576 字节 | `ab15b236b6e6b1b84292253541601f0f9bec755533ab1406160bb38ecc9171fc` |

`source/` 为构建提交 `96e406a` 的全部已跟踪文件（排除 `releases/`），不含 `appsettings.json` 等本机配置。

## Git

- Build Commit：`96e406a`（`build: 版本号升至 0.3.0`）
- Release Commit：本文件所在的发布提交
- Tag：`v0.3.0`
