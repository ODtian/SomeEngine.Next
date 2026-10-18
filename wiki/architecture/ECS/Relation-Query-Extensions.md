# Relation Query Extensions

关系语义、候选算法和结果接收分别通过 `IRelation<T1, …, Tn>`、`IRelationEnumerable<TRow>` 与 `IRelationOutput<TRow>` 表达。具体类型保留到执行，普通 C# 编译负责调用语义方法。组件绑定及集合规则见 [Relation Queries](Relation-Queries.md)，具体键和位实现见 [Key Relation Queries](Key-Relation-Queries.md)、[Bitmap Relation Queries](Bitmap-Relation-Queries.md)。

## 共同接入点

需要 ECS 组件绑定的算法实现 `IQueryRelationEnumerable<TRow>`。描述值保存 World、参与查询身份、具体索引和规则参数；`QueryCount` 与 `GetQuery(position)` 表达实际参与的成员查询。`AddReads` 汇总算法及语义读取，`Enumerate(RelationQueryViews, ref output)` 在现有查询作用域内访问数据。原生边适配的查询数量为零，仍通过同一访问视图读取端点和业务组件。

具体索引的候选通过成员视图确认参与资格，再使用已有组件绑定与终端。作用域、行表示、版本基线和输出状态的共同规则由 [Relation Queries](Relation-Queries.md) 维护。仅接收已有值或独立领域数据的枚举，也可直接实现 `IRelationEnumerable<TRow>`。

## 算法家族的扩展设计

下表是本规格的扩展设计。各行使用相同的数据、枚举和筛选职责说明接入方式；具体算法随对应工作负载交付。

| 算法家族 | 实现持有的数据 | 具体枚举入口 | 筛选位置 |
| --- | --- | --- | --- |
| 排序／范围连接 | 已有有序键、实体身份与区间边界 | 通过 `IQueryRelationEnumerable<TRow>` 合并有序输入或访问重叠区间 | 区间排除及实际候选行的语义判断 |
| 因子化积／多路连接 | 参与查询、键分组和部分绑定的身份元组 | 通过同一接口按分组逐步填入参与位置 | 部分绑定阶段的关系判断及完整行判断 |
| 空间网格 | 单元格、成员地址与几何范围 | 具体空间枚举访问相关单元格及其成员组合 | 单元格范围排除、类别摘要与叶子判断 |
| 空间扫描线 | 有序范围端点、活动成员与几何范围 | 具体空间枚举沿端点顺序产生重叠候选 | 活动范围排除、类别摘要与逐对判断 |

这些实现读取既有 QueryHandle 与具体索引。ECS 继续持有组件、查询缓存及原生键桶，具体算法持有自己的派生索引数据。排序键、范围、成员和摘要的更新沿用实体表变化、组件版本和对应系统基线；维护完成到枚举可见的顺序通过现有 Job 依赖表达。

扩展沿用 [Relation Queries](Relation-Queries.md) 的成员、位置、配对、投影和输出规则；通过公开结果与同输入手写实现验证语义等价。并行任务的访问及依赖沿用 [Readiness Admission](Readiness-Admission.md)，性能记录沿用 [Performance Evidence](../../harness/Harness-Performance-Evidence.md) 的实际成本与公共队列规则。
