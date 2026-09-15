# CloudFlow v0.6.1

发布日期：2026-09-14

## Fixed

- 顶栏账户区域原来显示账户展示名（`CloudAccount.DisplayName`），两个不同账户展示名完全相同时（真实复现：`piaohongji@outlook.com` 与 `piaohongji@live.cn` 展示名一样）无法从顶栏分辨当前生效的是哪一个账户。改为显示登录名（UPN，`CloudAccount.Username`）——在同一身份 Provider 下能唯一区分。展示名仍保留在悬浮提示（`AccountToolTip`）与账户切换菜单的副标题里，账户切换菜单本身和设置页账户列表此前就已经在展示名旁边带上了用户名，未受影响。

## Documentation

- `README.md` 新增「功能」一节：完整介绍多账号/多租户/多订阅、虚拟机全生命周期运维、资源清理（含 v0.6.0 新增的按列下拉筛选器）、统一 Operation Engine、任务中心、应用内 SSH 终端、成本洞察。
- `docs/user-guide.md` 补充说明："所有资源"/"资源组"页的筛选器已从纯搜索框升级为"名称搜索框 + 按列下拉筛选器"；删除确认框的目标资源高亮显示；删除失败展示清晰错误原因；顶栏账户区域显示登录名而非展示名。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 513 项全部通过（Core.Tests 93、App.Tests 43、Operation.Tests 55、Terminal.Tests 116、Azure.IntegrationTests 206）
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：本地启动 `CloudFlow-v0.6.1-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 无

## Git

- Commit：`708be74`
- Tag：`v0.6.1`
