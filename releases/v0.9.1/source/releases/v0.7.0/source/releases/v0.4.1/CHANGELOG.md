# CloudFlow v0.4.1

发布日期：2026-09-14

本版本修复正式版单文件 EXE 无法配置 Azure 登录服务的问题：发布包按约定不携带 `appsettings.json`（真实 ClientId 不随发布产物分发），此前界面也没有填写入口，只能看到「请复制 appsettings.example.json 为 appsettings.json 并填入 ClientId」而无从下手。现在可在「设置 → 账户 → 登录服务」中直接配置。

## Fixed

### 发布版可在设置页配置登录服务

- 「设置 → 账户 → 登录服务」：未配置时直接展开配置区，填写**应用（客户端）ID**后保存。
- **账户范围**改为选择而不是手填 `organizations`，两项与 Entra 应用注册「受支持的帐户类型」对应：
  - 多个组织（任何组织目录中的账户）→ `organizations`（默认）；
  - 单个组织（仅此组织目录中的账户）→ 须填写目录（租户）ID（GUID）或已验证域名。
- 不提供 `common` / `consumers`：ARM 只支持 Entra 组织账户，个人 Microsoft 账户仍走「添加个人 Microsoft 账户」。
- 配置保存到 `%LOCALAPPDATA%\CloudFlow\appsettings.json`（仅公开的客户端 ID 与租户，不含密钥），启动时优先于程序目录的 `appsettings.json`。
- 首次配置保存后立即生效，可直接添加工作或学校账户；修改已有配置提示重启后生效（已登录的令牌缓存绑定旧的应用注册）。
- 客户端 ID 非 GUID、单个组织未填有效目录时就地提示，不保存。
- 配置文件先写临时文件再替换，保留文件中其它配置项；该文件被手工改坏时程序退回程序目录配置照常启动，并写入崩溃日志。
- 未配置时的报错改为指向「设置 → 账户 → 登录服务」，不再要求复制模板文件。
- 状态行「登录范围」显示为「多个组织（organizations）」/「单个组织（…）」。

## Changed

- README「Azure 配置」改为两种方式：发布版在设置页填写；源码开发调试仍可用程序目录的 `appsettings.json`（gitignore）。

## Known Issues

- 在设置页实际保存配置并完成工作或学校账户登录的端到端验证**未执行**（避免用测试数据写入本机用户配置）；保存与校验逻辑由单元测试覆盖。
- 选择「单个组织」后显示目录（租户）ID 输入框的界面切换未实机点击验证，由绑定与静态测试保证。
- 批量删除与单项删除在真实 Azure 上的端到端验证仍**未执行**（沿用 v0.4.0）。

## Verification

- **Build**：`dotnet build CloudFlow.sln -c Release -t:Rebuild` → **0 警告，0 错误**。
- **Tests**：`dotnet test CloudFlow.sln -c Release --no-build` → **481 通过，0 失败，0 跳过**。
  - App.Tests：36（新增登录服务配置静态守护 3 项）
  - Core.Tests：82
  - Operation.Tests：48
  - Terminal.Tests：116
  - Azure.IntegrationTests：199（新增客户端 ID / 租户校验与配置文件保存 18 项）
- **UI**：单文件 EXE 在无 `appsettings.json` 的目录启动，设置页显示「未配置」并自动展开配置区；「账户范围」下拉含两个选项，默认「多个组织」。
- **Publish**：`dotnet publish src/CloudFlow.App/CloudFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:PublishDocumentationFiles=false`；发布目录仅含单个 EXE，不含 `appsettings.json`。
- **Standalone**：`CloudFlow-v0.4.1-win-x64.exe` 在未携带 `appsettings.json` 的目录启动，出现主窗口「CloudFlow」且响应正常，随后正常关闭（退出码 0）；过程中未生成用户配置文件。
- **Version**：EXE `ProductVersion` = `0.4.1+9c203c194537c0add2ec5bb4a8b3002b5f21ea5d`，`FileVersion` = `0.4.1.0`。
- **Platform**：Windows 10 19041+。
- **Architecture**：win-x64，自包含单文件，无需预装 .NET Desktop Runtime。

## Artifacts

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `CloudFlow-v0.4.1-win-x64.exe` | 223,454,829 字节 | `e38230475752f66a76733fe6adb0c78bbc2b8b7c6cd11a7075a33946f5035c08` |
| `CloudFlow-v0.4.1-win-x64.zip` | 86,123,513 字节 | `94acaf392e7482dd775c419c2d2787c46b73d9448ffa7e5ff57e08e415e2ab58` |

`source/` 为构建提交 `9c203c1` 的全部已跟踪文件（排除 `releases/`），不含 `appsettings.json` 等本机配置。

## Git

- Build Commit：`9c203c1`（`build: 版本号升至 0.4.1`）
- Release Commit：本文件所在的发布提交
- Tag：`v0.4.1`
