# CloudFlow v0.1.0

发布日期：2026-09-13

首个正式发布。P1 阶段（VM 运维 + 多订阅）功能面完整。

## Added

**Azure 接入与多账号**
- 多订阅清单（Azure Resource Graph）与跨订阅 VM 列表
- MSAL 设备码登录 + 嵌入式 Azure CLI 两条身份 Provider，可切换；账户切换后
  ArmClient 与订阅不串用（有隔离验证测试）
- Scope 选择器、账户菜单、租户发现

**VM 运维**（全部经 Operation Engine：Validate → Impact → Permission → Execute → Verify → Audit）
- 启动 / 重启 / 关机 / 解除分配 / 更改规格 / 创建磁盘快照
- 网络上下文聚合（NIC / VNet / Subnet / NSG / 出入站规则），开放与变更端口、删除规则
- **创建虚拟机与删除虚拟机** —— 本次纳入 P1，设计见 `docs/01-产品设计/…产品设计文档 v3.1.md` §87
- 任务中心与本地审计（`audit.jsonl`）

**SSH 终端与凭据**
- 应用级底部终端面板，多标签会话（WebView2 + xterm.js）
- 全局 SSH 凭据库（DPAPI 加密，元数据与密文分文件），设置页「凭据管理」分节
- 连接入口：VM 详情页、列表「⋯」菜单、列表标题行批量连接
- 批量连接：只连 Linux（Windows 跳过并如实告知）、完全串行、首次遇到的主机指纹合并确认
- 按 VM 记住连接配置（选了凭据点「连接」即记住，下次自动预选）
- 主机指纹两段式校验：首次记录需确认、指纹变化强警告、绝不静默接受

## Fixed

- 带 passphrase 的私钥凭据保存后**必然连不上** —— passphrase 从未被写入任何存储
- `SshCredentialInput` 是 record，编译器生成的 `ToString()` 会把明文密码与 passphrase 打印进日志
- 只读 DataGrid 单元格里 TwoWay 绑定**从不回写模型** —— 勾选状态只活在界面上，计数恒为 0
- 表头复选框不渲染：`DataGridTemplateColumn` 只设 `HeaderTemplate` 不设非空 `Header` 时，内容根本不画
- 表头内容的 DataContext 不继承自 DataGrid，普通绑定静默失效
- 自动刷新会静默清空用户勾了一半的名单（勾选现按 ResourceId 跨刷新保留）

## Changed

- 产品设计基准升为 **v3.1**：P1 范围纳入 Create VM / Delete VM（§80 清单与新增 §87）
- SSH 终端实现标准升为 **v1.2**：承载形态改为应用级底部面板，凭据改为全局库
- `MsalAuthConfig.DefaultClientId` 由硬编码的真实 App Registration ID 改为占位符
  `SET-YOUR-PUBLIC-CLIENT-ID`；真实值只放被 gitignore 的 `appsettings.json`

## Known Issues

- **批量连接与批量主机指纹确认框尚未在真实 Azure 上做过端到端验证**（代码与单测已就绪）
- **创建虚拟机的界面（两步向导）尚未实现** —— 引擎侧（Handler / 执行器 / 审批门）已完成，
  界面上「创建虚拟机」按钮仍是占位提示
- 删除虚拟机的 UI 入口（行菜单 + 连带资源勾选确认框）尚未实现，同上
- 快照入口仍是占位弹窗，而后端已实现（§80 清单中的 `Snapshot` 属于"后端有、入口无"）
- `MessageBox` 是系统对话框，不跟随应用主题，深色模式下观感不一致（已知限制，未改）

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告 0 错误**
- **Tests**：`dotnet test CloudFlow.sln -c Release` → **421 项通过 / 0 失败**
  （App.Tests 17 / Operation.Tests 25 / Core.Tests 82 / Terminal.Tests 116 / Azure.IntegrationTests 181）
- **Platform**：Windows First
- **Architecture**：win-x64（框架依赖发布，运行需要 .NET 8 桌面运行时）
- **未执行**：创建/删除虚拟机与批量连接的真机端到端验证（见 Known Issues）

## Git

- **Tag**：`v0.1.0` —— 本文件所在的提交即该标签所指向的提交
