# CloudFlow v0.7.0

发布日期：2026-09-14

## Added

- 系统托盘图标常驻：左键双击 / 右键菜单「打开 CloudFlow」还原并激活主窗口；右键菜单另有「查看任务」「设置」「退出 CloudFlow」。用 `System.Windows.Forms.NotifyIcon` 实现——WPF 没有原生的托盘图标控件，这是业内最通用、最稳妥的方式，不是新增第三方依赖（Windows Desktop SDK 自带，跟 WPF 同一个 SDK）。`UseWPF` 与 `UseWindowsForms` 同时打开会导致 `UserControl`/`ComboBox`/`Application`/`Brush`/`Size`/`Point` 等类型名两边隐式全局 using 冲突，通过 `<Using Remove>` 移除 `System.Windows.Forms`/`System.Drawing` 的隐式引用解决，只在需要 `NotifyIcon` 的 `MainWindow.xaml.cs` 里用命名空间别名显式引用。
- 设置页新增「通用」分节，两个开关：
  - 「开机时启动 CloudFlow」：读写 `HKEY_CURRENT_USER\...\Run` 注册表项（`WindowsStartupManager`），只读写当前用户、不需要管理员权限。真实生效状态以注册表为准，不重复落盘到 `settings.json`——避免用户通过 Windows 自带的"启动"设置页或任务管理器关闭它之后，两处记录互相打架。
  - 「关闭窗口时最小化到通知区域」：落盘到 `settings.json`（`AppSettings.MinimizeToTrayOnClose`），默认**关闭**——这是本设置存在之前没有的行为，不能替用户悄悄改变点右上角"×"的语义。开启后，`MainWindow.OnClosing` 拦截关闭并隐藏窗口（首次隐藏弹一次气泡提示），托盘菜单的「退出 CloudFlow」通过 `_isExiting` 标记绕开这层拦截、走正常的终端会话收尾与真正退出流程。

## Changed

- 设置页「通用」分节前置到分节条最前面（原来排第 4 位，和代码注释里"账户 → 通用偏好 → 功能设置 → 数据 → 关于"的既定顺序对不上）。
- 「外观」（主题：跟随系统/浅色/深色）并入「通用」，不再单列一个只有一项内容的分节——主题和开机启动、关闭到托盘同属"一次设置、长期不变"的应用整体偏好。复核过其余分节（列表与刷新、网络、操作与审批、本地数据、账户/凭据管理、关于），均维持独立，理由见提交记录。

## Fixed

- 标题栏单击无法拖动窗口：`IsInteractiveElement` 沿可视化树往上查找"是否点在控件上"时没有设终止边界，会一路查到 `Window` 本身——`Window` 继承自 `Control`，导致每次点击最终都会在链条顶端命中"是控件"，无论点哪里都被误判成"点在交互控件上"，`DragMove()` 因此永远不会被调用。这是一个长期存在的问题，不是本轮改动引入的回归。修复：把向上查找的终点限定在顶栏 `Border`（挂事件的 `sender`）本身，不再越界查到窗口。用真实鼠标事件模拟验证过：修复前窗口位置纹丝不动，修复后精确按位移量移动。
- 顶栏全局搜索语义错误：搜索框占位文字是"搜索资源、虚拟机…"，但原来无论输入什么关键字都只会跳到"虚拟机"列表——搜资源组、存储账户这类非虚拟机资源时，会被导到一个必然搜不到东西的虚拟机列表，看起来像是"搜索坏了"。改为导到"所有资源"（`AllResourcesViewModel`，本来就覆盖全部资源类型），并新增 `SetExternalFilter`：设置关键字的同时把类型/资源组/区域三个下拉复位到"全部"，避免上次残留的筛选条件悄悄限制这次搜索的结果。原有 `NavigateVirtualMachines(filter)` 的其它调用点（首页快捷入口、所有资源里点击虚拟机行、详情页返回列表）语义上就是"找虚拟机"，不受影响。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 529 项全部通过（Core.Tests 93、App.Tests 59、Operation.Tests 55、Terminal.Tests 116、Azure.IntegrationTests 206）
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：
  - 托盘图标注册验证：用 UI Automation 定位任务栏 `SystemTray.NormalButton`（Name="CloudFlow"），确认图标正常注册显示，未落入隐藏溢出区
  - 标题栏拖拽验证：用真实鼠标按下/移动/松开事件模拟拖拽，窗口位置按位移量精确变化
  - 本地启动 `CloudFlow-v0.7.0-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 无

## Git

- Commit：（见发布提交）
- Tag：`v0.7.0`
