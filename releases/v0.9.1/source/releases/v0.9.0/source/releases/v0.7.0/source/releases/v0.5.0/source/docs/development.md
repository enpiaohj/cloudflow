# 开发指南

面向想在本机构建、调试或修改 CloudFlow 的贡献者。想先理解整体架构，见
[`architecture.md`](architecture.md)；想贡献代码前的流程与规范，见根目录 [`CONTRIBUTING.md`](../CONTRIBUTING.md)。

## 环境要求

- Windows 10 19041 (2004) 或更新版本
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 或更新版本（可选，命令行 `dotnet` 已足够开发）
- 无需预先安装 Azure CLI——CloudFlow 自带独立的托管 Runtime（见 [`authentication.md`](authentication.md)）

## 克隆与构建

```bash
git clone https://github.com/enpiaohj/cloudflow.git
cd cloudflow
dotnet build CloudFlow.sln
```

首次构建会自动还原 NuGet 依赖，无需额外步骤。

## 运行

```bash
dotnet run --project src/CloudFlow.App
```

不配置任何 Azure 凭据也能直接运行——CloudFlow 默认是 **Demo 模式**：全部数据来自内存态的
Mock 服务，可以完整体验创建/删除虚拟机、资源清理等界面流程。要接入真实 Azure 账户，见
[`authentication.md`](authentication.md)。

## 运行测试

```bash
dotnet test CloudFlow.sln
```

改动只涉及 `CloudFlow.Core` 或 `CloudFlow.Operations` 时，可以只跑相关测试加快反馈：

```bash
dotnet test tests/CloudFlow.Core.Tests tests/CloudFlow.Operation.Tests
```

测试项目按层拆分：

| 项目 | 覆盖范围 |
| --- | --- |
| `CloudFlow.Core.Tests` | 平台核心模型（Scope、Resource、Operation 等） |
| `CloudFlow.Operation.Tests` | Operation Engine 流水线、各 Handler 的校验/影响分析逻辑 |
| `CloudFlow.App.Tests` | 界面层的静态守护测试（不启动真实 WPF 窗口，核对 XAML/ViewModel 源码约定） |
| `CloudFlow.Terminal.Tests` | SSH 会话、凭据保险库、主机指纹校验 |
| `CloudFlow.Azure.IntegrationTests` | Azure SDK 适配层；大部分是不需要真实网络的单元测试，
  少数标了 `[Trait("Category", "Integration")]` 的用例会访问真实网络（如 Azure CLI Runtime 的
  真实下载验证），默认测试命令会一并跑，运行较慢属正常现象 |

## 项目结构

见 [`architecture.md`](architecture.md#模块与代码组织) 的完整目录说明。几条对贡献代码有直接影响
的约定：

- **所有会改变 Azure 状态的写操作必须经过 Operation Engine**，禁止在 ViewModel 里直接调用
  `Azure.ResourceManager.*` 的写方法。新增一种操作的完整流程见
  [`provider-development.md`](provider-development.md)。
- **所有跨资源的查询方法统一接受 `ResourceScope`**，不要写 `GetVirtualMachines(subscriptionId)`
  这种绑死单订阅的签名。
- **真实实现与 Demo 模式的 Mock 实现共用同一个接口**，通过 `App.xaml.cs` 里的依赖注入切换。新增
  一个只读查询服务时，记得同时提供一份 Mock 实现，否则 Demo 模式会缺这块功能。
- **UI 用 MVVM**：View（XAML）只做绑定与样式，逻辑放在 ViewModel（`CommunityToolkit.Mvvm` 的
  `[ObservableProperty]` / `[RelayCommand]`），涉及 DataGrid 只读单元格的双向绑定（如行内勾选框）
  时注意 `Cf.DataGrid` 样式统一设了 `IsReadOnly="True"`，`TwoWay` 绑定不会真正提交，需要在代码后置
  里手动写回（参考已有页面里 `RowCheck_Click` 这类实现）。

## 调试建议

- **Demo 模式优先**：大部分界面改动不需要真实 Azure 账户就能验证，Mock 服务的种子数据在各
  `Modules.*/Services/Mock*Service.cs` 里。
- **日志**：应用运行时的调试日志走 `Microsoft.Extensions.Logging`（`AddDebug()`），可以在 Visual
  Studio 的"输出"窗口查看；崩溃日志落盘在 `%LOCALAPPDATA%\CloudFlow\logs\`，保留最近 10 份。
- **本地数据目录**：`%LOCALAPPDATA%\CloudFlow\` 下有应用设置、任务历史、审计日志、SSH 凭据库、
  已保存 Scope 等全部本地状态，删除整个目录相当于重置到全新安装状态（不会影响 Azure 账户本身）。
- **接入真实 Azure 调试**：先完成 [`authentication.md`](authentication.md) 的配置；也可以用
  `tools/CloudFlow.Spike`（独立于主应用的技术验证控制台）单独验证登录与 Resource Graph 查询链路，
  不需要跑起整个 WPF 应用：
  ```bash
  # 需要先配置 tools/CloudFlow.Spike/appsettings.json（参考同目录的 appsettings.example.json）
  dotnet run --project tools/CloudFlow.Spike
  ```

## 发布产物（了解即可，通常不需要贡献者操作）

正式发布使用 Semantic Versioning，`releases/vX.Y.Z/` 下归档每个版本的完整源码快照与变更记录；
编译好的可执行文件通过 GitHub Releases 分发，不提交进仓库。完整变更历史见根目录
[`CHANGELOG.md`](../CHANGELOG.md)。
