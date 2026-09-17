# CloudFlow v0.2.0

发布日期：2026-09-13

本版本补全 P1 的创建虚拟机界面与真实 ARM 预配执行链路，并将成本洞察改为只展示真实 Azure Cost Management 数据。

## Added

### 创建虚拟机

- 两步「创建虚拟机」向导：基本信息 → 网络与凭据。
- 支持在**已有**资源组与子网中创建 Linux 或 Windows VM；不创建资源组、VNet、子网或 NSG。
- 支持 Ubuntu Server 24.04 / 22.04、Windows Server 2022，以及 `Standard_B1s`、`Standard_B2s`、`Standard_B2ms`、`Standard_D2s_v5`。
- 可选创建 Standard 静态公网 IP；未选择时只创建私网 NIC。
- Linux 支持 SSH 公钥或管理员密码；Windows 仅允许密码方式。
- 密码先写入 DPAPI 全局凭据库，`OperationRequest.Payload` 仅保存 `credentialId`，绝不持久化明文密码。
- ARM 执行器按公网 IP → NIC → VM 顺序创建；OS 盘随 VM 创建并设置随 VM 删除。
- 执行前拒绝同名 VM、NIC 或公网 IP，避免 `CreateOrUpdateAsync` 意外覆盖既有资源。
- Mock 模式会真实更新内存 VM 清单；创建 / 删除 Job 终态后列表自动刷新。

### 发布产物

- 新增 Windows x64 自包含单文件程序：`CloudFlow-v0.2.0-win-x64.exe`。
- 发布过程不再复制本机 `appsettings.json`，避免将本机 Azure ClientId / TenantId 带入发布产物。

## Changed

- 列表页「创建虚拟机」不再显示 P1 范围外的占位提示，改为打开实际创建向导。
- 创建入口补充 UI Automation 元数据，便于自动化与辅助功能定位。
- 待审批 Job 的持久化约束注释改为指向实际守护测试 `JsonJobStorePendingRequestTests`。

## Fixed

- 未登录时成本洞察不再显示 `$482.21`、模拟环比等演示账单数据；现显示空态并提示登录 Azure。

## Known Issues

- 创建 VM 的真实 Azure 端到端 UI 验证**未执行**；本版本仅完成了编译、单元 / 静态测试和单文件程序启动验证。
- 批量 SSH 连接与批量主机指纹确认框的真实 Azure 端到端验证**未执行**。
- 快照入口仍是占位弹窗；后端链路已实现但界面入口尚未接入。
- VM 详情页「概览」改「摘要」、信息排版与内层滚动优化尚未实现。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **430 通过，0 失败**。
  - App.Tests：22
  - Core.Tests：82
  - Operation.Tests：29
  - Terminal.Tests：116
  - Azure.IntegrationTests：181
- **Standalone**：`CloudFlow-v0.2.0-win-x64.exe` 已在未携带 `appsettings.json` 的发布目录启动并出现主窗口；随后正常关闭。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Artifacts

| 文件 | SHA-256 |
| --- | --- |
| `CloudFlow-v0.2.0-win-x64.exe` | `2180a894dbfacb029cebcb419641cf8fa5a10bf868bbdc3f23d4dcf34027e7dc` |
| `CloudFlow-v0.2.0-win-x64.zip` | `12ed7257f59b13c4abc2e86b534b9269dbc07ebaf45dc886bd93cd713d7cd6a9` |

## Git

- Commit：本文件所在的发布提交
- Tag：`v0.2.0`
