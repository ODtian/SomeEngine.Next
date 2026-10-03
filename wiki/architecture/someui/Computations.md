# Computations

## Definition
计算是会因依赖变化而重新执行的代码及其依赖记录。

| 计算 | 是否有自身节点 |
|---|---|
| 视图的 `Build` | 有，挂在视图实体上 |
| Solid 的 Effect、Memo、属性绑定 | 没有，存于所属视图实体的计算列表（sparse 组件） |

## Invariants
- 两种计算共用同一套依赖登记、版本检查与调度。
- 属性绑定写目标节点组件副本的目标字段，例如 `Write<Text>(node).Value = ...`；计算结束时与当前值比较提交，值相同则保留版本。提交规则见 [[architecture/someui/Build-Scope|Build Scope]]。
- Memo 在被读取时检查自身依赖，过期则在嵌套作用域中当场重算，外层只依赖 Memo 本身；结果相等时不使下游失效。
- 在某个计算中创建的计算随它释放。

## Relations
[[architecture/someui/Build-Modes|Build Modes]] · [[architecture/someui/Solid-Frontend|Solid Frontend]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
