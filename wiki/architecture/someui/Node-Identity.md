# Node Identity

## Definition
节点身份决定一次构建声明的节点与上次的哪个实体对应。所有写法统一使用调用位置身份：源生成器用拦截器为每个构建体内产出节点的调用提供调用位置常量。

| 场景 | 身份来源 |
|---|---|
| 普通 struct、Compose、SwiftUI 写法 | 调用位置常量加出现次数；`if`/`else` 两个分支的调用位置不同，身份互相独立 |
| Solid 写法 | 组件只运行一次，身份在创建时确定；`Show`、`For`、`Index` 自行决定子节点身份 |
| 列表 | 显式 `using (Key(id))` |

## Invariants
- 实际身份是 key 加该 key 在当前父节点下本次的出现次数，保存在子实体的 `ElementKey` 组件中。上次的子节点就是层级中的现有子节点，不另存组合记录。
- 底层公开 `View<T>(in T view, ulong key)`；未经拦截的 `View<T>(in T view)` 使用 key 0，等同于按出现顺序匹配，并由开发检查报告未拦截的调用。
- 一个产出带状态节点的辅助函数，在同一位置被有条件地多次调用时，需要 `Key` 或 `[Inline]` 分组。
- 同一父节点下同 key 的兄弟数量变化且它们带有局部状态时，由开发检查报告。

## Relations
[[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Compose-Frontend|Compose Frontend]] · ADR 0013
