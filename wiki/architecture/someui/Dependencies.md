# Dependencies

## Definition
依赖是计算读取时登记的 `(来源, 版本)`。每帧在视图重建阶段比较版本，变化的计算才重新执行。

| 来源 | 登记方式 | 失效条件 |
|---|---|---|
| ECS 组件 | `Read<T>(entity)`；版本由 `World.WriteVersion<T>` / `SparseWriteVersion<T>` 读取 | 该实体该组件的写版本变化（见 [[architecture/ECS/Change-Tracking|Change Tracking]]） |
| 实体结构 | `Has<T>(entity)` | 组件被添加或移除 |
| 查询成员 | `Each(query)` | 结果集的实体增删或顺序变化，与组件值无关 |
| 环境数据 | `ReadUp<T>()` | 找到的祖先组件变化，或树结构变化后重新查找 |
| 对象 | `Depend(cell)`，或读取 `State<T>.Value` | `VersionCell.Version` 变化 |

## Invariants
- 只有带版本的数据能被依赖。外部数据通过持有 `VersionCell` 接入，不必进入 ECS。
- 写入只推进版本，不推送通知；跨线程写入无需派发，下一次重建阶段即可看到。
- ECS 依赖存于节点的 `DynamicBuffer<Dependency>`；对象依赖存为 `(VersionCell 引用, 版本)` 数组，均不装箱。
- 按组件类型的全局写版本未变时，该类型的所有依赖整批跳过。
- 依赖粒度是整个组件。需要更细的粒度时按变化频率拆分组件（例如点击计数 `Clicks` 与悬停状态 `Hover` 分开），不提供字段级依赖。
- 列表的父计算只依赖 `Each` 的成员，每行是读取自身实体的子视图。

## Relations
[[architecture/someui/State|State]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0010
