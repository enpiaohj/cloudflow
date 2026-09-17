# CloudFlow v0.9.1

发布日期：2026-09-17

## Fixed

- 单文件发布版（PublishSingleFile EXE）SSH 连接失效：单文件发布不打包 Content 文件——发布输出里 `Terminal/Assets` 是散落在 exe 旁边的散文件，历次发布打包时只带走了 exe 本身，用户拿到的发布版没有这批资产，终端渲染层（WebView2/xterm.js）起不来，详情页/列表/批量所有 SSH 连接入口全部失效（曾表现为永远停在「待连接」）。修复：终端资产同时以 EmbeddedResource 随 `CloudFlow.Terminal` 程序集走，`TerminalAssetStore` 优先用程序旁目录（开发构建行为不变），没有则释放到 `%LOCALAPPDATA%\CloudFlow\Terminal\Assets` 并返回该目录（幂等：同名同尺寸跳过，删缺即补）；两条路都拿不到时抛可行动异常，`SshTerminalView` 初始化失败会把一线异常的具体原因带进「连接失败」提示。开发构建因输出目录一直有资产兜底，此问题在自测中从未暴露，仅在发布版复现。

## Verification

- Build：`dotnet build CloudFlow.sln -c Release -t:Rebuild` — 0 警告 / 0 错误
- Tests：`dotnet test CloudFlow.sln -c Release --no-build` — 566 项全部通过（Core.Tests 93、App.Tests 70、Operation.Tests 55、Terminal.Tests 123、Azure.IntegrationTests 225）
- Platform：Windows 11 Pro
- Architecture：win-x64（self-contained、single-file）
- Runtime 验证：
  - 发布产物二进制中确认包含终端资产（特征串命中）
  - 裸目录（仅 exe、无 Terminal/Assets、无缓存）启动正常，无崩溃日志
  - **实测**（维护者）：详情页「连接」、虚拟机列表行「连接」、批量「连接选中」三个入口均正常建立 SSH 会话

## Known Issues

- 无

## Git

- Commit：（见发布提交）
- Tag：`v0.9.1`
