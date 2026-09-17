# CloudFlow v0.4.0

发布日期：2026-09-14

本版本为「资源组」「所有资源」增加批量删除；首页改为随窗口自适应宽度与高度，读取状态改为动态指示；Scope 选项文案规范为「所有订阅」；并修复单项删除失败时该行仍从列表消失的问题。

## Added

### 批量删除（资源组 / 所有资源）

- 两个列表新增勾选列与表头全选；虚拟机行不可勾选，仍走专门的虚拟机删除流程。
- 勾选后工具栏出现「删除所选（N）」按钮；未勾选时不显示，不做常驻删除按钮。
- 执行流程：逐项提交 → 合并为一个确认框（列出全部目标 Resource ID 与各自影响分析，须输入「删除 N 个资源组」或「删除 N 项资源」才能继续）→ 逐项执行，并显示「第 i/N 个」进度。
- 每一项仍是独立 Job，各自走完整 Operation Engine 流水线（Validate → Impact → Permission → Execute → Verify → Audit）并留审计记录；合并的只是确认这一步。
- 取消确认或提交中途出错时，作废本轮已提交的待审批任务，不在任务中心遗留待审批项。
- 执行结束后只移除真正成功的行；失败项在页面提示条逐条列出原因。
- 只勾选一项时退回单项删除（按名称确认）。
- 「所有资源」按依赖顺序删除：网卡 → 其它资源 → 虚拟网络 → 网络安全组 / 路由表，避免同批内因依赖关系必然失败。
- 执行按顺序进行（不并发）：引擎的待审批表不是线程安全集合，且顺序执行时进度可读。

## Changed

- 首页去掉 `MaxWidth=1200` 限宽居中，窗口拉宽 / 最大化时卡片随窗口铺满，与其它页面一致。
- 首页外框高度跟随可视区，「最近操作 / 注意项」一行按剩余高度伸缩：窗口变高时显示更多条目并在卡片内滚动，底部不再留大片空白；窗口较矮时整页照常滚动。
- 首页「最近操作」最多显示条数由 6 提高到 20；两块空态垂直居中。
- 首页右上角「正在读取…」前增加旋转图标（`Cf.SpinningIcon`）。
- Scope 下拉「全部可访问订阅」改为「所有订阅」，与 Azure 门户订阅筛选器措辞一致；`ShellViewModel` 中重复的字面量收拢为常量。
- 影响分析对话框增加批量模式；目标资源与影响面区域限高滚动，避免多项时对话框超出屏幕。

## Fixed

- 删除资源组 / 单个资源执行失败时，该行仍从列表移除、看起来像已删除。引擎把失败记在 Job 上照常返回、不抛异常，现改为按 Job 状态判断，只有成功才移除。

## Known Issues

- 批量删除在真实 Azure 上的端到端验证**未执行**；已完成编译、单元 / 静态测试，以及真实账户下勾选列展示的界面核对（未执行任何删除）。
- 删除资源组 / 删除单个资源（单项）在真实 Azure 上的端到端验证仍**未执行**。
- 批量删除执行期间若直接关闭确认窗口，已开始的逐项执行会在后台继续，剩余未执行的待审批任务会被作废。
- 同时创建多台虚拟机时，进度提示可能互相串台（沿用 v0.3.0）。
- 窗口 Mica 背景未启用。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **460 通过，0 失败，0 跳过**。
  - App.Tests：33
  - Core.Tests：82
  - Operation.Tests：48
  - Terminal.Tests：116
  - Azure.IntegrationTests：181
- **UI**：在 1900×1000、1320×700、1040×620 三种窗口尺寸下截图核对首页布局；核对「所有订阅」文案、读取状态旋转图标，以及「所有资源」勾选列（虚拟机行复选框禁用）。
- **Publish**：`dotnet publish src/CloudFlow.App/CloudFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:PublishDocumentationFiles=false`；发布目录仅含单个 EXE，不含 `appsettings.json`。
- **Standalone**：`CloudFlow-v0.4.0-win-x64.exe` 在未携带 `appsettings.json` 的目录启动，出现主窗口「CloudFlow」且响应正常，随后正常关闭（退出码 0）。
- **Version**：EXE `ProductVersion` = `0.4.0+ead33b7437ef3ad9e1a756d50ea5087fba9c7768`，`FileVersion` = `0.4.0.0`。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Artifacts

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `CloudFlow-v0.4.0-win-x64.exe` | 223,442,541 字节 | `3a240285af7962ce8dd10a4727715a802cf4cceab6941ec0391e9f2e9e52151d` |
| `CloudFlow-v0.4.0-win-x64.zip` | 86,124,585 字节 | `5003acff4e2957599b025825d0488b08c2488ab87ce592a9e6257e27fb4110cb` |

`source/` 为构建提交 `ead33b7` 的全部已跟踪文件（排除 `releases/`），不含 `appsettings.json` 等本机配置。

## Git

- Build Commit：`ead33b7`（`build: 版本号升至 0.4.0`）
- Release Commit：本文件所在的发布提交
- Tag：`v0.4.0`
