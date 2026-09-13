# CloudFlow

> CloudFlow for Microsoft Azure — 面向 Microsoft Azure 的多账号、多租户、多订阅、模块化云资源智能运维平台。

## 当前状态

当前版本：**v0.1.0**（首个正式发布）。首个交付模块：**Compute / Virtual Machine Operations Center**。

- 产品设计基准：`docs/01-产品设计/20260913-CloudFlow 云资源智能运维平台产品设计文档 v3.2.md`
  （**架构冲突时以该文档为准**；范围变更走「另存新文件 + 升版号」，见文档的修订记录）
- UI 概念基准：`docs/01-产品设计/UI/UI概念图1.png`、`UI概念图2.png`
- 变更记录：根目录 `CHANGELOG.md`；每个版本的完整记录见 `releases/vX.Y.Z/CHANGELOG.md`

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | WPF + WPF-UI (Fluent Design) + CommunityToolkit.Mvvm，.NET 8 |
| 架构 | Platform Services + Resource Modules（v3.1：Account → Tenant → Scope → Resource → Operation） |
| 身份认证 | MSAL.NET（WAM Broker 预留）+ 嵌入式 Azure CLI 两条身份 Provider |
| 资源发现 | Azure Resource Graph（已接入；**未登录时**回退 Mock 演示数据，已登录时真实查询失败会如实报错、不回退） |
| 终端 | WebView2 + xterm.js（应用级底部面板，多标签会话） |
| 写操作 | 统一 Operation Engine（Validate → Impact → Permission → Execute → Verify → Audit） |

## 本地开发

```bash
# 构建
dotnet build CloudFlow.sln

# 测试
dotnet test CloudFlow.sln

# 运行桌面应用（当前为 Demo 模式，使用模拟数据）
dotnet run --project src/CloudFlow.App

# P0 技术验证（真实 MSAL 登录 + 订阅发现 + Resource Graph 查询）
# 需要先配置 tools/CloudFlow.Spike/appsettings.json（参考 appsettings.example.json）
dotnet run --project tools/CloudFlow.Spike
```

## Azure 配置

复制 `src/CloudFlow.App/appsettings.example.json` 为 `appsettings.json`，填入 Entra ID App Registration 的
`ClientId`（Public Client，无 Secret）。**`appsettings.json` 已被 .gitignore 排除，不得提交真实配置。**

还没有 App Registration 时，在 Azure Portal 创建：

1. Microsoft Entra ID → 应用注册 → 新注册：名称 `CloudFlow`，受支持账户类型选**任何组织目录中的账户**（多租户），
   重定向 URI 选**公共客户端/本机 (mobile & desktop)** 并填 `http://localhost`
2. 注册后进入「身份验证」页，底部「**允许公共客户端流**」设为**是**并保存
3. 「概述」页复制**应用程序(客户端) ID** 填入 `appsettings.json` 的 `ClientId`，重启应用

登录说明：

- 「设置 → 使用 Microsoft 登录」走 MSAL 交互登录（系统浏览器），登录后虚拟机清单自动切换为 Resource Graph 真实数据
- 登录状态持久化在 `%LOCALAPPDATA%\CloudFlow\msal-token-cache.bin`（DPAPI 加密），重启应用自动静默恢复，无需再次登录
- 「设置 → 退出登录」仅清除本机 Token 缓存，不影响 Microsoft 账户本身

## 目录结构

```
src/
  CloudFlow.Core          # 平台核心模型：Identity / Scopes / Resources / Operations / Errors
  CloudFlow.Data          # 本地持久化（Saved Scopes、Audit 等）
  CloudFlow.Operations    # Operation Engine（操作总线）
  CloudFlow.Azure         # Azure 适配层：MSAL 认证、ARM、Resource Graph
  CloudFlow.Terminal      # SSH 会话与凭据库（DPAPI 保险库、主机指纹校验、终端控件）
  Modules/
    CloudFlow.Modules.Compute   # 计算模块（Virtual Machines）
    CloudFlow.Modules.Network   # 网络模块（NIC / NSG / Port 管理）
  CloudFlow.App           # WPF 桌面应用
tests/                    # Core / Operation 单元测试、Azure 集成测试
tools/CloudFlow.Spike     # P0 技术验证控制台
runtime/                  # P0 嵌入式 Azure CLI（本机下载/解包，不入库）
```
