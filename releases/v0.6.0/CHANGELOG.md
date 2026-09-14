# CloudFlow v0.6.0

发布日期：2026-09-14

> 本版本合并了 v0.5.2~v0.5.9 这 8 个未推送到 GitHub 的本地迭代版本——它们是同一次测试会话里
> 连续发现问题、连续修复产生的中间版本，从未发布也从未被外部使用，因此不再作为独立版本保留，
> 合并为这一个正式版本发布。上一个实际发布过的版本是 v0.5.1。

## Fixed

### 删除操作稳定性

- "所有资源"/"资源组"页删除资源/资源组时的 `ItemContainerGenerator` 崩溃（"某个 ItemsControl 与它的项源不一致"）：批量删除与单项删除各自独立复现过一次，根因最终定位到搜索过滤器在列表自身 `CollectionChanged` 事件分发过程中被同步刷新，与 DataGrid 正在处理的变更冲突。两个页面统一改为延后到事件分发结束后再刷新过滤器。
- 应用崩溃后卡在"执行中"的任务，下次启动时会被识别为失败，不再永久显示"进行中"。
- 删除资源/资源组失败时不再展示完整 HTTP 诊断转储（Status/ErrorCode/原始 Content/全部 Header），改为摘出 Azure 错误信封里的一句话（通常会点名是被谁引用/占用导致删不掉）。覆盖 `DeleteAsync` 与执行前存在性预检查 `ExistsAsync` 两条路径——后者是本轮新增的存在性预检查引入的遗漏点，真实复现过 `NoRegisteredProviderFound`。
- 两个删除 Handler（`DeleteResourceGroupHandler`、`DeleteResourceHandler`）补上执行前的存在性预检查，避免对已被别处删掉的目标重复撞 404。
- "资源组"/"所有资源"页点击刷新按钮时绕过内部 10 分钟内存缓存，不再显示已删除的陈旧数据。

### 创建虚拟机向导

- 可编辑下拉框（订阅/资源组/区域/规格/镜像）在用户打字覆盖预选项后仍会误用旧的 `SelectedItem`——WPF 的可编辑 ComboBox 不会因为文本被覆盖就清空 `SelectedItem`。真实复现：资源组框输入新名称 `RG-LT1-SG` 打算新建，创建时最终却用了已有的 `RG-AC-KR`。统一改为 `SelectedItemIfTextUnchanged<T>` 兜底：只有当显示文本仍与该选项的展示文本完全一致时才信任 `SelectedItem`，否则以用户实际输入的文本为准。

## Added

- 删除确认对话框中的"目标资源"改用危险色边框/背景 + 加粗加大字号高亮显示。
- 创建虚拟机向导的资源组下拉框支持边打字边筛选已有资源组。
- "所有资源"页新增类型、资源组、区域三个下拉筛选器；"资源组"页新增区域、订阅两个下拉筛选器；均保留原有名称搜索框（子串包含匹配），与各下拉按 AND 组合。每个下拉旁边有固定的说明性文字（"类型"/"资源组"/"区域"/"订阅"，不随选中值变化），下拉默认值也带字段名（如"全部类型"），确保无论是否已选中具体值都能看出这个下拉对应哪一列。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 512 项全部通过（Core.Tests 93、App.Tests 42、Operation.Tests 55、Terminal.Tests 116、Azure.IntegrationTests 206）。过程中 `AzureCliRuntimeInstallerRealDownloadTests` 的临时目录清理多次出现瞬时文件占用报错，单独重跑与全量重跑均通过，判定为环境级瞬时占用，非本次改动引入，值得后续专门排查
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：本地启动 `CloudFlow-v0.6.0-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 本版本新增/修改的所有 UI（筛选器、创建向导下拉框、删除确认高亮）均未做交互式 UI 回归验证。测试过程中一次自动化点击误触了另一个正在运行实例上已打开的确认框，导致真实删除了 Azure 账户中的 `NetworkWatcherRG`（空资源组，Azure 会自动重建）；此后本轮会话对存活的真实账户停止了任何自动化点击，验证仅覆盖编译与单元/静态测试。

## Git

- Commit：`03baf39`
- Tag：`v0.6.0`（本地打好，暂不推送 GitHub——等待本地 EXE 测试确认）
