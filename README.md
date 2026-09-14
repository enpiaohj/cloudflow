# CloudFlow

> CloudFlow for Microsoft Azure — 面向 Microsoft Azure 的多账号、多租户、多订阅、模块化云资源智能运维平台。

![CloudFlow 首页](docs/screenshots/home.png)

## 当前状态

当前版本：**v0.5.1**。首个交付模块：**Compute / Virtual Machine Operations Center**；另含资源组 / 所有资源的清理入口。

- 变更记录：根目录 `CHANGELOG.md`；每个版本的完整记录见 `releases/vX.Y.Z/CHANGELOG.md`
- 许可证：[GPL-3.0](LICENSE)；随源码分发的第三方组件见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)

## 文档

| 文档 | 内容 |
| --- | --- |
| [`docs/user-guide.md`](docs/user-guide.md) | 用户手册：界面各部分怎么用 |
| [`docs/authentication.md`](docs/authentication.md) | 认证说明：如何配置账户登录 |
| [`docs/architecture.md`](docs/architecture.md) | 公开架构：核心设计原则与模块划分 |
| [`docs/development.md`](docs/development.md) | 开发指南：环境搭建、构建、测试 |
| [`docs/provider-development.md`](docs/provider-development.md) | 如何新增一种资源/操作 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 贡献指南 |

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | WPF + WPF-UI (Fluent Design) + CommunityToolkit.Mvvm，.NET 8 |
| 架构 | Platform Services + Resource Modules（Account → Tenant → Scope → Resource → Operation） |
| 身份认证 | MSAL.NET（工作/学校账户）+ 嵌入式 Azure CLI（个人账户，设备码流程，按需下载） |
| 资源发现 | Azure Resource Graph（已接入；**未登录时**回退 Mock 演示数据，已登录时真实查询失败会如实报错、不回退） |
| 终端 | WebView2 + xterm.js（应用级底部面板，多标签会话） |
| 写操作 | 统一 Operation Engine（Validate → Impact → Permission → Execute → Verify → Audit） |

详见 [`docs/architecture.md`](docs/architecture.md)。

## 快速开始

```bash
# 构建
dotnet build CloudFlow.sln

# 测试
dotnet test CloudFlow.sln

# 运行桌面应用（未配置账户时为 Demo 模式，使用模拟数据）
dotnet run --project src/CloudFlow.App
```

完整开发环境说明见 [`docs/development.md`](docs/development.md)。

## 账户配置

CloudFlow 用 Entra ID App Registration（Public Client，无 Secret）登录工作或学校账户，个人
Microsoft 账户走嵌入式 Azure CLI 设备码登录。完整配置步骤、登录说明与故障排查见
[`docs/authentication.md`](docs/authentication.md)。

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
    CloudFlow.Modules.Network   # 网络与通用资源模块（NSG / 资源组 / 通用资源删除）
  CloudFlow.App           # WPF 桌面应用
tests/                    # 按项目拆分的单元测试与 Azure 集成测试
tools/CloudFlow.Spike     # 独立技术验证控制台（不随发布产物分发）
docs/                     # 用户与贡献者文档
```

详见 [`docs/architecture.md`](docs/architecture.md#模块与代码组织)。
