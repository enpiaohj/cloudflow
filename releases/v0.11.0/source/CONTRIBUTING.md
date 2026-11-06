# 贡献指南

感谢你对 CloudFlow 感兴趣。本篇说明如何提交问题反馈与代码贡献。

## 开发环境

见 [`docs/development.md`](docs/development.md)：环境要求、构建、运行、测试命令。

## 提交 Issue

- 报告 Bug 时请附上：CloudFlow 版本号（设置页「关于」可查看）、Windows 版本、重现步骤、
  预期行为与实际行为。
- 涉及真实 Azure 账户的问题，请**不要**在 Issue 里粘贴任何真实的订阅 ID、租户 ID、资源名称或
  截图中的账户信息；用占位符替代（如 `<subscription-id>`）。
- 功能建议请说明具体使用场景，而不只是"希望支持 XXX"。

## 提交代码

1. Fork 本仓库，基于 `main` 创建分支，分支名建议 `feature/<内容>`、`fix/<内容>` 这类前缀。
2. 改动前先读 [`docs/architecture.md`](docs/architecture.md)，了解核心架构原则——尤其是
   "所有写操作必须经 Operation Engine"这一条，PR 里出现绕过它的改动会被要求重做。
3. 提交前本地跑通：
   ```bash
   dotnet build CloudFlow.sln
   dotnet test CloudFlow.sln
   ```
4. Commit Message 遵循 [Conventional Commits](https://www.conventionalcommits.org/)：
   `feat:` / `fix:` / `docs:` / `refactor:` / `test:` / `build:` / `ci:` / `chore:`，说明要能独立
   表达改动目的，避免 `update`、`修改` 这类无意义描述。
5. 提交 Pull Request，说明改动内容、动机，以及如何验证（跑了哪些测试、是否手动验证过界面）。

## 代码规范

- **架构原则不可绕过**：写操作必须经 `IOperationHandler` + Operation Engine（见
  [`docs/provider-development.md`](docs/provider-development.md)）；跨资源查询统一接受
  `ResourceScope`。
- **保持现有风格**：C# 用文件顶部隐式 `using`（`ImplicitUsings`）、可空引用类型已全局启用
  （`Nullable=enable`），新代码不应引入可空警告。
- **新增只读服务需要同时提供 Mock 实现**，否则 Demo 模式会缺这块功能，界面在未登录状态下会
  出现该功能不可用的空白。
- **注释使用中文**，与现有代码保持一致；说明"为什么这样做"比复述"这行代码做了什么"更有价值——
  尤其是修复过的真实 Bug、踩过的坑，值得留一句注释避免以后被重新踩一次。
- **不引入新的第三方依赖**，除非确有必要；引入时需要在 PR 里说明理由，并确认许可证与本项目的
  GPL-3.0 兼容（宽松许可证如 MIT / Apache-2.0 / BSD 通常没有问题）。

## 测试要求

- 新增或修改 Handler、Service 等逻辑代码，需要配套单元测试，见
  [`docs/provider-development.md#测试约定`](docs/provider-development.md#测试约定)。
- 界面改动如果引入了新的静态可验证约定（如"删除入口必须在三个点菜单里，不能是常驻红按钮"这类
  产品决策），建议补充 `CloudFlow.App.Tests` 里的静态守护测试，防止未来被无意间改回去。
- PR 不应包含失败的测试，也不应通过删除测试或降低断言强度来让测试"变绿"。

## 许可证

CloudFlow 采用 [GPL-3.0](LICENSE)。提交 Pull Request 即表示你同意你的贡献以同一许可证发布。
随源码分发的第三方组件许可证见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## 安全问题

如果你发现的是安全漏洞（而不是一般 Bug），请不要在公开 Issue 中披露细节，改为通过仓库的
私信/联系方式报告，给维护者时间修复后再公开。
