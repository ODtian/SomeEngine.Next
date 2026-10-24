---
status: accepted
---

# 视图是 struct，前端只在协调方式上不同

SomeUI 的每个界面节点都是一个视图：实现 `IView` 的 struct，字段是参数，`Build` 是构建方法。参数作为组件保存在节点实体上，并且就是系统消费的节点数据，内置元素与用户视图使用同一机制。源生成器为每个视图生成同名函数，`new` 形式与函数形式等价。

支持 Compose、SwiftUI、SolidJS 三种 C# 写法，它们只在协调方式上不同：构建模式（`Rerun` 或 `Once`）、节点身份来源（调用位置或创建时确定）、变化送达方式（重跑后比较参数，或计算直接写目标字段）。元素、修饰、布局、状态、异步与环境数据共用一套，不为还原原版语法另做多套。

闭包只用于 Solid 前端的计算和组件主动调用的内容函数；重跑型前端用 struct 视图与 `using` 表达结构，避免每次重跑分配闭包。本决策替代 [ADR 0007](0007-reactive-computations-use-closures.md)。

用户在 2026-10-03 确定。设计细节见 Wiki [[architecture/SomeUI|SomeUI]]。
