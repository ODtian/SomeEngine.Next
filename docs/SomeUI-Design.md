# SomeUI 设计总览

状态：已决定（2026-10-03）。本文只做总览与阅读路径；每条设计事实由 Wiki 的 [SomeUI](../wiki/architecture/SomeUI.md) 原子页拥有，需求见 [整体设计约束](SomeUI-Requirements.md)。

## 一句话

界面树上的每个节点都是一个 struct 视图，参数就是节点数据；计算在读取时登记带版本的依赖，每帧在重建阶段拉取校验；Compose、SwiftUI、Solid 三种写法只在协调方式上不同，其余全部共用；库与用户代码对等，定制通过组合完成。

## 关键决策

| 决策 | 记录 |
|---|---|
| SomeUI 与 ImGui 的职责 | [ADR 0008](adr/0008-someui-and-imgui-roles.md) |
| 视图是 struct，前端只在协调方式上不同（替代 ADR 0007） | [ADR 0009](adr/0009-struct-views-and-frontend-reconciliation.md) |
| 依赖按版本拉取校验，状态不限制线程 | [ADR 0010](adr/0010-pull-based-versioned-dependencies.md) |
| 库与用户代码对等，定制通过组合完成 | [ADR 0011](adr/0011-composition-over-replacement.md) |
| 异步构建在视图重建阶段恢复 | [ADR 0012](adr/0012-async-build-resumes-in-rebuild-phase.md) |
| 只面向 NativeAOT，拦截器统一提供调用位置身份 | [ADR 0013](adr/0013-compose-interception-under-aot.md) |
| 动画是引擎级通用库：核心加属性动画、姿势动画 | [ADR 0014](adr/0014-general-animation-core.md) |

## 阅读路径

1. 节点与构建：Views → Build Scope → Build Modes → Node Identity → Child Source → Component Storage。
2. 响应式：Dependencies → State → Computations → Rebuild Schedule → Async Build。
3. 前端：Frontends → Compose / SwiftUI / Solid。
4. 组合与数据：Composition → Modifiers → Data Writers → Input and Behaviors。
5. 布局与渲染：Layout → Lists → Text → Animation（界面衔接，核心见 Wiki `architecture/Animation`）→ Accessibility → Render Path。
6. 工程：Assemblies → Checks → Testing → Performance → Open Questions。

以上页面均位于 `wiki/architecture/someui/`。2026-10-03 的候选方案保留在 `wiki/history/SomeUI/`，只用于解释历史取舍。
