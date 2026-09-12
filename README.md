# CloudFlow

> CloudFlow for Microsoft Azure — 面向 Microsoft Azure 的多账号、多租户、多订阅、模块化云资源智能运维平台。

## 当前状态

开发初期（P0/P1）。首个交付模块：**Compute / Virtual Machine Operations Center**。

- 产品设计基准：`docs/01-产品设计/20260912-CloudFlow 云资源智能运维平台产品设计文档 v3.0.md`
- UI 概念基准：`docs/01-产品设计/UI/UI概念图1.png`、`UI概念图2.png`

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | WPF + WPF-UI (Fluent Design) + CommunityToolkit.Mvvm，.NET 8 |
| 架构 | Platform Services + Resource Modules（v3.0：Account → Tenant → Scope → Resource → Operation） |
| 身份认证 | MSAL.NET（WAM Broker 预留） |
| 资源发现 | Azure Resource Graph（P1 接入，当前为 Mock） |
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
`ClientId` / `TenantId`（Public Client，无 Secret）。**`appsettings.json` 已被 .gitignore 排除，不得提交真实配置。**

## 目录结构

```
src/
  CloudFlow.Core          # 平台核心模型：Identity / Scopes / Resources / Operations / Errors
  CloudFlow.Data          # 本地持久化（Saved Scopes、Audit 等）
  CloudFlow.Operations    # Operation Engine（操作总线）
  CloudFlow.Azure         # Azure 适配层：MSAL 认证、ARM、Resource Graph
  Modules/
    CloudFlow.Modules.Compute   # 计算模块（Virtual Machines）
    CloudFlow.Modules.Network   # 网络模块（NIC / NSG / Port 管理）
  CloudFlow.App           # WPF 桌面应用
tests/                    # Core / Operation 单元测试、Azure 集成测试
tools/CloudFlow.Spike     # P0 技术验证控制台
```
