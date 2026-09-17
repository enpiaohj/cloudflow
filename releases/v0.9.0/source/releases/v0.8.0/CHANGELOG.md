# CloudFlow v0.8.0

发布日期：2026-09-15

## Added

- "新建入站/出站规则"对话框新增「操作」（允许 / 拒绝）选项：此前 `NsgRuleDraft` 没有 `Action` 字段，Mock 与 ARM 两个执行器都写死 `Access = Allow`，对话框上也没有这一项，永远只能创建 Allow 规则，跟 Azure 门户自己的"新建规则"面板不一致。现在贯穿对话框 → payload → `OpenPortHandler` → 执行器（Mock/ARM）→ `SecurityRuleData.Access`。

## Changed

- 影响面分析（`AssessImpactAsync`）按 Allow/Deny 分开措辞：Allow + 任意对端是"新增暴露面"（入站是"整个 Internet 能访问到"，出站是"这台机器能访问任意目标"）；Deny + 任意对端反而是"新增一条可能连带挡住其他规则的封堵"——原来那句"将对 Internet 暴露该服务"套在 Deny 规则上是说反的。`SubtitleText`/`WarningText` 同理按 Allow/Deny 分开。
- 规则名称改成自动按"操作 + 端口 + 方向"拼接（如 `AllowVnetInBound` / `DenyAllOutBound` 那种 Azure 内置规则的命名方式），例如 `Allow8080InBound`，不再是固定写死的 `AppAccess`；用户一旦手改过名字就不再自动覆盖（跟 Azure 门户自己"新建规则"面板的体验一致）。
- 端口默认值统一为 8080（原来入站 8443、出站 443 两套不必要的不一致）。
- 默认对端改为「任意 (Any)」（原来入站默认"我的当前 IP"、出站默认"虚拟网络"）——实测这不是用户真实的常见用法；对端为"任意"始终会触发审批，安全阀不靠这个默认值。

## Fixed

- 虚拟机详情页的操作反馈条补上手动关闭按钮：虚拟机列表 / 所有资源 / 资源组三个页面早就有这个关闭按钮（`DismissInfoCommand`），唯独详情页漏掉了——"更改端口"等操作完成后的反馈只能靠离开再回到这个 VM 才会被清掉。详情页这条反馈条比其余三个页面多一层耦合——它同时驱动"批准执行/作废"两个按钮，所以关闭时一并清空 `PendingApprovalJob`，不会变成"按钮消失但状态还在"的死角；任务本身不会丢失，"任务"页对 `WaitingApproval` 的任务有独立的批准/作废入口。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 539 项全部通过（Core.Tests 93、App.Tests 66、Operation.Tests 55、Terminal.Tests 116、Azure.IntegrationTests 209）
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：本地启动 `CloudFlow-v0.8.0-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 无

## Git

- Commit：（见发布提交）
- Tag：`v0.8.0`
