# 认证说明

CloudFlow 支持两类 Azure 身份，互相独立、可以同时登录多个账户：

| 账户类型 | 认证方式 | 适用场景 |
| --- | --- | --- |
| 工作或学校账户 | MSAL.NET，系统浏览器交互式登录 | 企业 / 组织 Azure 订阅 |
| 个人 Microsoft 账户 | 嵌入式 Azure CLI，设备码登录 | 个人订阅（如 Visual Studio 订阅） |

CloudFlow **不保存 Azure 密码**，不提交、不上传任何账户凭据；登录状态只保存在本机的 Windows 受保护
存储中。

## 工作或学校账户

### 配置 Entra 应用注册（首次使用前，一次性）

CloudFlow 用 Entra ID App Registration（Public Client，无 Client Secret）登录，需要你自己在 Azure
门户注册一个应用：

1. Microsoft Entra 管理中心 → **应用注册** → **新注册**：
   - 名称任意（如 `CloudFlow`）
   - **受支持的账户类型**：按你的场景选择——多个组织可选，或仅限你自己的组织
   - **重定向 URI**：平台选 **公共客户端/本机 (mobile & desktop)**，填 `http://localhost`
2. 注册完成后进入应用的**「身份验证」**页，把底部**「允许公共客户端流」**设为**是**并保存。
3. 回到**「概述」**页，复制**应用程序(客户端) ID**（一个 GUID）。

### 填入 CloudFlow

两种方式，任选其一：

- **发布版（单文件 EXE，推荐）**：打开「设置 → 账户 → 登录服务」，粘贴应用程序(客户端) ID；
  账户范围按你在应用注册第 1 步选的类型对应选择（多个组织 / 单个组织 + 目录（租户）ID），保存后
  立即生效。配置写入 `%LOCALAPPDATA%\CloudFlow\appsettings.json`，只包含这个公开的客户端 ID，
  不含任何密钥。
- **源码开发调试**：复制 `src/CloudFlow.App/appsettings.example.json` 为 `appsettings.json` 并填入
  `ClientId`。该文件已被 `.gitignore` 排除，不会被提交。两处都配置时，`%LOCALAPPDATA%` 下的配置
  优先。

修改**已经生效**的登录服务配置（换一个应用注册）需要重启 CloudFlow——运行中的登录客户端已经按
旧配置创建，热切换会让"已登录账户"与"新的应用注册"对不上。

### 登录与数据存储

- 「设置 → 账户 → 添加工作或学校账户」发起 MSAL 交互式登录，系统浏览器完成后自动关闭，虚拟机等
  清单随即切换为通过 Resource Graph 查询到的真实数据。
- 登录状态（Token Cache）持久化在 `%LOCALAPPDATA%\CloudFlow\msal-token-cache.bin`，用 Windows
  DPAPI 加密，仅当前 Windows 用户账户能解密；重启应用会自动静默恢复登录，无需重新走一遍浏览器。
- 「设置 → 退出登录」只清除本机这份缓存，不会影响 Microsoft 账户本身，也不会影响你在其它设备/
  应用上的登录状态。

## 个人 Microsoft 账户

个人账户（如 Outlook/Hotmail 邮箱对应的 Visual Studio 订阅）不走 MSAL 交互式登录——部分网络环境下
它依赖的 WAM Broker 组件不可用，改用 Azure CLI 的**设备码流程**：CloudFlow 内置一份托管的 Azure CLI
Runtime，完全独立于系统是否安装过 Azure CLI，也不会污染 `%USERPROFILE%\.azure` 这个全局位置——每个
个人账户各自一份隔离的 Profile 目录，互不共享登录状态。

### 首次使用：自动下载 Runtime

这份 Runtime 体积约 90 MB，为了不让所有人都要下载它（多数人只用工作/学校账户），CloudFlow **不会**
随发布的 EXE 一起分发，而是在你第一次点击「添加个人 Microsoft 账户」时自动下载：

- 下载源是 Azure CLI 官方 GitHub Release 的 x64 便携版 ZIP（`Azure/azure-cli` 仓库，MIT 许可证），
  版本固定为 `2.90.0`——这是本项目验证过完整登录链路的版本，不会跟着"最新版"漂移。
- 下载完成后会校验 SHA-256，与官方发布值不一致时直接放弃安装，不会解压或执行任何未经校验的内容。
- 只需要下载一次，落盘在 `%LOCALAPPDATA%\CloudFlow\Runtime\AzureCLI`，之后每次添加/使用个人账户
  都直接复用。
- 下载过程中登录对话框会显示进度文字；网络失败会给出明确原因（而不是笼统的"失败"），可以直接重试。

### 登录流程

1. 「设置 → 账户 → 添加个人 Microsoft 账户」（或顶栏账户菜单的同名入口）。
2. 弹出的对话框会显示一个验证网址（固定为 `https://microsoft.com/devicelogin`）和一段设备码，
   可以一键复制或直接打开浏览器。
3. 在浏览器里打开该网址、输入设备码、完成登录后，对话框会自动检测到登录成功并继续读取订阅列表。
4. 登录过程中可以取消；登录成功、进入"读取订阅"阶段后不再可取消。

### 数据存储与隔离

- 每个个人账户对应 `%LOCALAPPDATA%\CloudFlow\Identity\cli-profile-<GUID>\` 下一个独立目录，Token、
  订阅缓存等全部隔离在各自目录内。
- 移除某个个人账户只删除它自己的 Profile 目录，不影响其它已登录账户，也不影响 Microsoft 账户本身。

## 故障排查

| 现象 | 可能原因 / 处理方式 |
| --- | --- |
| 「尚未配置登录服务」 | 还没有在「设置 → 账户 → 登录服务」填写应用（客户端）ID，见上文配置步骤 |
| 添加工作/学校账户后立刻失败，报 AADSTS 相关错误 | 检查应用注册的「受支持的账户类型」与 CloudFlow
  里选的账户范围是否一致；检查「允许公共客户端流」是否已设为「是」 |
| 「Azure CLI Runtime 缺失」 | 网络无法访问 GitHub Releases，或下载被中断；重新点一次「添加个人
  Microsoft 账户」会重试下载 |
| 设备码登录卡在"正在启动登录" | 检查网络是否能直连 `login.microsoftonline.com` 与
  `github.com`（下载 Runtime 需要）；企业代理环境下确认代理未拦截这两个域名 |
| 个人账户登录成功但看不到任何订阅 | 该 Microsoft 账户下没有可管理的 Azure 订阅，或订阅权限不在
  这个账户名下 |
