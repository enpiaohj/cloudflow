# Changelog

本项目遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。
每个正式版本的**完整**变更记录见 `releases/vX.Y.Z/CHANGELOG.md`（含验证结果与已知问题）。

---

## v0.1.0 — 2026-09-13

首个正式发布。P1 阶段（VM 运维 + 多订阅）功能面完整。

**主要能力**
- 多订阅清单与跨订阅 VM 列表（Azure Resource Graph）；MSAL 设备码 + 嵌入式 Azure CLI 两条身份
- VM 运维全部经 Operation Engine（启停 / 重启 / 关机 / 解除分配 / 改规格 / 快照 / 网络规则）
- 应用级底部终端面板与多标签 SSH 会话（WebView2 + xterm.js）
- 全局 SSH 凭据库（DPAPI，元数据与密文分文件）；连接入口覆盖详情页、列表菜单与批量连接
- 主机指纹两段式校验：首次记录需确认、变化强警告、绝不静默接受
- **创建 / 删除虚拟机纳入 P1 范围**（设计文档 v3.1 §87；引擎侧完成，界面待实现）

**验证**：`dotnet build -c Release -t:Rebuild` 0 警告 0 错误；421 项测试全部通过

**主要已知问题**
- 批量连接与创建/删除虚拟机的界面尚未端到端验证（创建/删除的 UI 亦未实现）
- 快照入口仍是占位弹窗，后端已就绪

完整清单见 [`releases/v0.1.0/CHANGELOG.md`](releases/v0.1.0/CHANGELOG.md)。

---

## 版本与快照约定

- 正式发布使用 `MAJOR.MINOR.PATCH`，Tag 为 `vX.Y.Z`（不加产品名前缀）。
- 每次正式发布创建 `releases/vX.Y.Z/`：`source/`（源码快照）与 `CHANGELOG.md` **入库**；
  **二进制产物不入库**，作为 GitHub Release 的附件交付。
- 已发布的快照不可覆盖：发现 Bug 时发新版本，而不是修改旧快照。
