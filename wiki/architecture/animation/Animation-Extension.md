# Animation Extension

## Definition
动画系统是库：内置的值类型、曲线、节点和求值系统只是用公开 API 写成的其中一种实现，用户的扩展与它们地位相同。

| 扩展 | 方式 |
|---|---|
| 值类型 | 写一个实现 `IValueOps<T>` 的 struct，把 `TrackSystem<T, TOps>` 加入调度（需要合成时再加 `ComposeSystem<T, TOps>`） |
| 曲线 | 写 `Curve` 数据，或提供函数指针 |
| 图节点 | 写一个调用构建器原语的普通函数 |
| 运行时行为（物理驱动、沿路径运动等） | 写自己的组件加系统，读同一个 `Timeline`，并设置自己的 `Running` 位 |
| 默认配置 | `AnimationSystems.AddDefaults(schedule)` 只是把内置系统加入调度的便利函数，可以不用 |

## Invariants
- 没有注册表、管理类或内部入口。

## Relations
[[architecture/animation/Value-Ops|Value Ops]] · [[architecture/animation/Timeline|Timeline]]
