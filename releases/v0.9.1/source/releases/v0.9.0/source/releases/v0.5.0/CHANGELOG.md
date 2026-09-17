# CloudFlow v0.5.0

发布日期：2026-09-14

本版本为个人 Microsoft 账户登录补上首次使用时自动下载 Azure CLI Runtime 的能力（此前所有版本
这条登录路径实际上只在开发机上能用）；仓库采用 GPL-3.0 许可证；补齐面向使用者与贡献者的公开
文档；产品设计过程文档迁出仓库并清理了对应的 Git 历史，为转为 Public 仓库做准备。

## Added

### 个人账户登录：按需下载 Azure CLI Runtime

- 新增 `AzureCliRuntimeInstaller`：首次点击「添加个人 Microsoft 账户」且本机没有 Runtime 时，
  自动从 Azure CLI 官方 GitHub Release 下载 x64 便携版 ZIP（约 90 MB），下载完成后校验
  SHA-256，与官方发布值不一致时直接放弃、绝不解压或执行未经校验的内容；校验通过后解压到
  `%LOCALAPPDATA%\CloudFlow\Runtime\AzureCLI`，之后复用，不需要重复下载。
- 版本固定为 `azure-cli 2.90.0`——与本项目 P0 阶段验证过完整登录链路（登录 → 订阅发现 →
  ARM Token → ARM 查询）的版本一致，不随"最新版"漂移。
- `AzureCliRuntimeManager` 的 Runtime 查找候选路径新增这个下载目录；未找到 Runtime 时的提示
  文案改为指向"添加个人 Microsoft 账户会自动下载"，不再要求用户手动安装 Azure CLI。
- 下载/解压进度复用已有的登录对话框状态行展示，未新增界面。

### 公开文档

新增 `docs/` 下六份文档与根目录 `CONTRIBUTING.md`：

- `docs/user-guide.md` —— 用户手册，含界面截图
- `docs/authentication.md` —— 认证说明：两类账户配置步骤、登录流程、数据存储位置、故障排查
- `docs/architecture.md` —— 公开架构：核心模型、Operation Engine 流水线、模块划分
- `docs/development.md` —— 开发指南：环境要求、构建/测试、项目结构、调试建议
- `docs/provider-development.md` —— 如何新增一种资源/操作，含可运行的 worked example
- `CONTRIBUTING.md` —— 贡献指南：Issue/PR 流程、代码规范、测试要求

README 精简为导航页，指向以上文档。

## Changed

- 仓库改用 **GPL-3.0** 许可证（根目录 `LICENSE`）；随源码分发的 `xterm.js` 系列文件按 MIT 要求
  在新增的 `THIRD-PARTY-NOTICES.md` 中附上完整许可证文本。
- 产品设计过程文档（范围演进历史、内部技术验证记录、UI 视觉方案迭代、SSH 实现标准）迁出本仓库，
  改由维护者的私有工作区管理；`docs/` 目录现在只保留可以公开分发的文档。
- 对应地清理了 Git 历史：本次发布之前，仓库历史中一份早期提交曾短暂包含真实的 Entra 应用
  客户端 ID，以及内部验证记录中的真实个人账户邮箱、租户 ID、订阅 ID；连同上述迁出的设计文档，
  已从全部历史提交与此前 5 个 Release 快照（`v0.1.0`～`v0.4.1`）中一并移除并重新发布对应 Tag。
  移除前已创建本地备份，移除范围经过逐提交、逐文件树比对验证，除目标内容外未引入任何其它改动。
- `CLAUDE.md` / `AGENTS.md` / `README.md` 的产品基准说明同步调整为指向新的公开架构文档与私有
  工作区安排。

## Known Issues

- 补充的公开文档目前只含一张 Demo 模式首页截图；虚拟机列表、资源组等页面的截图尚未提供
  （受限于当前开发机会自动恢复真实账户登录状态，未在不冒暴露真实资源名称风险的前提下补齐）。
- 批量删除、单项删除、创建虚拟机在真实 Azure 上的端到端验证仍未在本次发布验证中重新执行
  （功能自 v0.4.0/v0.4.1 起未变动）。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **488 通过，0 失败，0 跳过**。
  - App.Tests：36
  - Core.Tests：82
  - Operation.Tests：48
  - Terminal.Tests：116
  - Azure.IntegrationTests：206（含一项真实网络端到端集成测试：实际下载官方 Azure CLI Runtime、
    校验 SHA-256、解压，并用解出来的 `az.cmd` 真跑 `az version` 确认报告版本为 2.90.0）
- **Publish**：`dotnet publish src/CloudFlow.App/CloudFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:PublishDocumentationFiles=false`；发布目录仅含单个 EXE，不含 `appsettings.json`。
- **Standalone**：`CloudFlow-v0.5.0-win-x64.exe` 在未携带 `appsettings.json` 的目录启动，出现
  主窗口「CloudFlow」且响应正常，随后正常关闭（退出码 0）。
- **Version**：EXE `ProductVersion` = `0.5.0+65567d6f8ba55981b15ebaae5caffcccc12589f7`，
  `FileVersion` = `0.5.0.0`。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Artifacts

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `CloudFlow-v0.5.0-win-x64.exe` | 223,463,021 字节 | `51f2f5108e0fd8baa8469c7e982aea89770ce164b6acaeb514cc75ceed654e17` |
| `CloudFlow-v0.5.0-win-x64.zip` | 86,127,217 字节 | `a32d45da5ea47c5a9d142b7e1fb11007f100001ed9f98b2a992d43f1a4b5a813` |

`source/` 为构建提交 `65567d6` 的全部已跟踪文件（排除 `releases/`），不含 `appsettings.json` 等
本机配置。

## Git

- Build Commit：`65567d6`（`build: 版本号升至 0.5.0`）
- Release Commit：本文件所在的发布提交
- Tag：`v0.5.0`
