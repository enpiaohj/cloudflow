# CloudFlow v0.5.1

发布日期：2026-09-14

本版本修复批量删除资源组/资源时的一处真实崩溃，并改善批量删除确认框里目标资源的显示方式。
纯 Bug 修复，不含新功能。

## Fixed

### 批量删除崩溃：`InvalidOperationException：某个 ItemsControl 与它的项源不一致`

真实账户上批量删除 3 个以上资源组/资源时会崩溃（崩溃日志定位到 `AllResourcesGrid`：
`ItemContainerGenerator` 内部计数与 `ObservableCollection` 实际状态不一致，"累积计数 18 与
实际计数 19 不相同"）。

- 根因：批量执行循环里每删成功一项就立刻从列表移除，"await 让出线程 → 下一次移除"这种节奏
  的连续快速删除，会让 DataGrid 的内部生成器与集合实际状态失步。
- 修复：循环内只记录成功的行，整批执行完毕后一次性用新集合替换列表，DataGrid 只收到一次
  变更通知。
- 影响范围：仅「资源组」「所有资源」页面的**批量**删除（一次勾选 3 项及以上）；逐项单独删除
  不受影响。

### 批量删除确认框：目标资源显示改善

- 此前直接堆全部目标的完整 Azure Resource ID（一长串 `/subscriptions/.../providers/...`
  路径），批量场景下几十行完全没法读；改为"名称（区域）"这种人类可读格式。
- 首个修复版本一度只显示区域的中文名，随后按反馈补回真实值：格式最终定为
  「名称（区域代码 · 中文名）」，如 `rg-test（koreacentral · 韩国中部）`——真实值放前面，
  中文名跟在后面方便读，不会因为中文对照表可能有遗漏或歧义而丢掉可核对的真实值。
- 单个资源删除的确认框不受影响，仍显示原始 Resource ID（供需要精确核对的场景）。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **489 通过，0 失败，0 跳过**。
  - App.Tests：37（新增批量确认框显示与移除时机的静态守护测试）
  - Core.Tests：82
  - Operation.Tests：48
  - Terminal.Tests：116
  - Azure.IntegrationTests：206
- **Publish**：`dotnet publish src/CloudFlow.App/CloudFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:PublishDocumentationFiles=false`；发布目录仅含单个 EXE，不含 `appsettings.json`。
- **Standalone**：`CloudFlow-v0.5.1-win-x64.exe` 在未携带 `appsettings.json` 的目录启动，出现
  主窗口「CloudFlow」且响应正常，随后正常关闭（退出码 0）。
- **Version**：EXE `ProductVersion` = `0.5.1+ee79d33fe97a528c9f7871fddfb9dbaa7cf902b4`，
  `FileVersion` = `0.5.1.0`。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Known Issues

- 修复后的批量删除代码路径未在真实 Azure 账户上重新执行端到端验证（原崩溃是从真实账户的
  崩溃日志中定位复现的，修复逻辑已通过静态测试与全量单元测试验证，但未重新触发一次真实的
  批量删除进行现场回归）。

## Artifacts

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `CloudFlow-v0.5.1-win-x64.exe` | 223,467,117 字节 | `ea88778f96756c3ebc4d1219900e1afa937ffb8b857a36c0fe7279c0d7920591` |
| `CloudFlow-v0.5.1-win-x64.zip` | 86,124,695 字节 | `20c0a75a8c5995d13bc60cc75f56f278968158913d980eb1fa0cfdd53411ca80` |

`source/` 为构建提交 `ee79d33` 的全部已跟踪文件（排除 `releases/`），不含 `appsettings.json`
等本机配置。

## Git

- Build Commit：`ee79d33`（`build: 版本号升至 0.5.1`）
- Release Commit：本文件所在的发布提交
- Tag：`v0.5.1`
