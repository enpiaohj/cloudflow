# CloudFlow v0.6.2

发布日期：2026-09-14

## Fixed

- 顶栏账户名截断：登录名换成邮箱地址后，原有 `MaxWidth="130"` 装不下较长的邮箱域名（真实复现：`piaohongji@live.cn` 能完整显示、`piaohongji@outlook.com` 被截断），调宽到 220。
- 深色模式右键菜单左边一条竖着的白条：第一版只重写了 `MenuItem` 的模板，白条依旧在——真正的占位列其实在 `ContextMenu` 自己的默认模板里（经典 Aero2 主题给整个菜单预留的一条贯穿全高的图标背景带，用来对齐所有菜单项的图标，跟每一项各自的 `MenuItem` 模板是两回事）。这一版把 `ContextMenu` 的模板也整个重写（`Border` + `ScrollViewer` + `ItemsPresenter`），不再使用系统默认的 Popup 外壳。`Separator` 的默认模板同样有一条会露白的浅色高光线，一并处理。
- "所有资源"页第一条记录右键第一次没反应、右键别的行之后再回头右键第一条又恢复正常：根因是 `DataGrid.RowStyle` 的 `Setter` 用 `StaticResource` 引用 `ContextMenu`——这个 Setter 属于所有行共用的同一个 `Style` 对象，`StaticResource` 只在 `Style` 生成时解析一次，所有行会拿到同一个 `ContextMenu` 实例；页面没开行虚拟化，所有行几乎同时生成，会一起抢这唯一一份实例的逻辑父级归属，最先生成的那一行（通常是第一条）因此第一次右键没反应。改为 `DataGrid.LoadingRow` 事件里逐行各自 `FindResource` 一次（资源标了 `x:Shared="False"`，每次调用都是全新实例），各行拿到真正独立的实例，跟"⋯"按钮那条路径（每行的 `DataTemplate` 各自实例化一次）原理一致。同一套坑理论上也存在于虚拟机列表页（默认开着行虚拟化，没那么容易复现，但代码模式完全一样），一并改成同样的写法。
- `AzureCliRuntimeInstallerRealDownloadTests` 集成测试的清理逻辑偶发文件占用报错（本轮会话第 4 次遇到）：测试进程（Python 解释器）退出后，操作系统释放其对文件句柄的时机不严格跟 `WaitForExit` 同步，紧接着删除临时目录会偶发 `IOException`。加短暂重试，不是产品代码的 Bug，是测试清理时序问题。

## Added

- "所有资源""资源组"两个列表补齐整行右键菜单（此前只有"⋯"按钮能开菜单，整行右键没反应）。所有资源页的虚拟机行没有删除入口，整行右键在这类行上不会绕开这道防线（`LoadingRow` 里按 `IsVirtualMachine` 把这类行的 `ContextMenu` 置空）。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 518 项全部通过（Core.Tests 93、App.Tests 48、Operation.Tests 55、Terminal.Tests 116、Azure.IntegrationTests 206），含之前偶发失败的 `AzureCliRuntimeInstallerRealDownloadTests`
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：本地启动 `CloudFlow-v0.6.2-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 无

## Git

- Commit：`0816ff8`
- Tag：`v0.6.2`
