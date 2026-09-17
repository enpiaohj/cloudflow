# CloudFlow v0.9.0

发布日期：2026-09-17

## Added

- 「关于」分节规范化：产品名 + 副标题标题区，信息表（版本与运行模式、开发者 enpiaohj（GitHub）、源码仓库超链接、许可证 GPL-3.0），隐私与数据披露保留在分节底部。版本号仍取自程序集 `InformationalVersion`，不硬编码；仓库地址由开发者名拼出（`AppInfo` 常量，单一来源）；超链接显式交给系统 Shell 以默认浏览器打开。

## Changed

- 全应用 UI 文案统一规范（系统性核对所有页面 XAML、7 个对话框、全部 ViewModel 消息后修正）：
  - 术语统一：「公共 IP」→「公网 IP」（详情页两处标签、资源类型目录）——同一页此前同时存在「复制公网 IP」与「公共 IP」两种叫法。
  - 引号统一：用户可见文案中的全角弯引号 "" 全部改为仓库既有的「」风格（删除规则确认框、影响面审批对话框的取消提示、新建规则对话框的 Allow/Deny 风险提示与校验错误，共 10 处）；代码注释不动。
  - 省略号规范：打开需要继续输入的对话框的按钮/菜单统一补「…」——新建规则（详情页头部 + 入站/出站规则卡 + 首页快捷入口）、更改端口、更改规格、编辑凭据、添加个人 Microsoft 账户；执行类/确认类按钮（创建规则、删除、清除）不加，符合 Windows 桌面规范。
  - 同义句统一：两处「作废」tooltip 的审计措辞对齐为「记入审计日志」；连接对话框与凭据编辑对话框的 DPAPI 披露句合并为设置页同一句；passphrase 提示「即 Passphrase。私钥没有密码则留空。」→「即 Passphrase，没有则留空。」。
  - 数字区间连字符统一：「100 – 4096」→「100–4096」，与「1–65535」一致。

## Fixed

- SSH 连接失败不再永远停在「待连接」：连接由终端渲染层（WebView2/xterm.js）就绪后发起（行列数确定后再连），渲染层起不来时此前的失败处理是三重黑洞——异常只往不存在的终端里写提示、视图拿着 NullLogger 无从排查、会话停在 Idle 标签永远显示「待连接」。现在渲染层初始化失败会将会话转入 Failed（标签显示「连接失败」，悬停可见原因），`TerminalPanel` 创建视图与会话改用面板 VM 的真实日志器；重新点「连接」即换全新会话重试。
- 嵌入式 Azure CLI 瞬态网络错误处理加强：取令牌、列订阅等幂等读操作对网络瞬断（连接被重置 / WinError 10054 / 代理超时 / DNS 解析失败等）自动重试最多 3 次（指数退避）；重试耗尽后给出「网络连接中断…请检查网络连接（或代理）后重试」的可行动提示，不再把 az 的 Python traceback 原样抛给用户；非网络失败（权限/参数/账户问题）不重试、保留「退出码 + 脱敏详情」。启动时网络瞬断不移除个人账户登记（`AzureCliException.IsTransientNetwork` 传至 `InitializeAsync`），界面退回演示数据并提示下次启动自动恢复；仅账户级失效才 `ForgetAccount`。

## Docs

- `docs/user-guide.md`：新增「端口规则（NSG）」一节（新建入站/出站规则的 Allow/Deny、自动命名、默认值与审批语义、更改端口、删除规则）与「系统托盘」一节（双击/右键菜单/关闭到托盘行为）；SSH 终端一节补充连接失败的表现与重试方式；「关于」设置项描述更新。
- `docs/authentication.md`：新增「网络波动时的行为」一节（自动重试、启动不丢账户的边界）；故障排查表补「网络连接中断，无法获取访问令牌」一行。
- `docs/architecture.md`：个人账户身份链路补充 `AzureCliFailure` 归因与重试策略摘要。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 562 项全部通过（Core.Tests 93、App.Tests 70、Operation.Tests 55、Terminal.Tests 119、Azure.IntegrationTests 225）
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：本地启动 `CloudFlow-v0.9.0-win-x64.exe`，进程正常驻留，无崩溃日志产生

## Known Issues

- 无

## Git

- Commit：（见发布提交）
- Tag：`v0.9.0`
