# 安全策略

## 支持的版本

| 版本 | 支持情况 |
| --- | --- |
| 0.9.x | ✅ 接收安全修复 |
| < 0.9.0 | ❌ 请升级到最新版 |

## 如何报告安全漏洞

**请不要通过公开的 Issue / Discussion 报告安全问题。**

优先使用 GitHub 的私密漏洞报告功能：仓库页 → **Security** → **Report a vulnerability**；
或在联系时说明来意，由维护者提供一个私密沟通渠道。

报告时请尽量包含：影响的版本、复现步骤、影响评估（能造成什么 / 不能造成什么）、
如有可能请附最小复现或 PoC。维护者会在 **72 小时内**确认收到，并协商修复时间线与披露节奏
（默认原则：修复发布后再公开细节）。

## 安全设计要点（供审计参考）

CloudFlow 是访问真实 Azure 订阅的桌面运维工具，以下是与安全直接相关的设计决策：

- **不保存 Azure 密码**：工作/学校账户走 MSAL 公共客户端流（无 Client Secret），
  Token 保存在本机 Windows 受保护存储；个人 Microsoft 账户走嵌入式 Azure CLI 设备码流程，
  Token 隔离在各自独立的 Profile 目录内。
- **SSH 凭据**：密码 / 私钥以 Windows 当前用户身份 DPAPI 加密后存放在本机保险库，
  其他 Windows 账户与其他机器无法解密；明文只在连接期间存活，绝不写入日志 / 审计 / 异常消息。
- **日志脱敏**：所有进入日志 / 异常消息 / 审计的 CLI 输出先经过脱敏
  （`AzureCliOutputRedactor`），掩盖 JSON 形态的 token / secret / password 值。
- **对外网络请求**：默认唯一的对外请求是「自动查询我的公网 IP」（api.ipify.org /
  icanhazip.com），可在设置中关闭；除此之外应用不会向 Microsoft 或任何第三方发送使用数据。
- **终端渲染**：本地资产经 WebView2 虚拟主机（https）加载，浏览器能力（右键菜单 / DevTools /
  状态栏等）全部关闭以缩小攻击面。
- **写操作链路**：所有写操作必须经 Operation Engine
  （Validate → Impact → Permission → Execute → Verify → Audit），禁止按钮直连 Azure SDK；
  高危操作（删除等）的确认不可被审批策略关闭。

## 范围界定

- 本仓库**不包含**任何真实 ClientId / TenantId / 凭据 / 订阅数据；`appsettings.json`
  只存在于本地（已 gitignore），模板见 `appsettings.example.json`。
- 仓库**不包含**设计过程文档（范围演进、内部验证记录、UI 迭代），它们存放在维护者的
  私有工作区。
