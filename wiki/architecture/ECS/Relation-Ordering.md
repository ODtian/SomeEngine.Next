# Relation Ordering

Relation order 属于 `(relation type, endpoint, adjacency role)` 的局部 membership，不属于 edge-global order 或整个 relation type。

每个 shard 独立选择 ordered/unordered。Ordered shard 保存自身 order metadata；unordered shard 只保存 packed edge/other-endpoint entries，不持续维护 order key/index。

Policy conversion 只重建目标 shard；旧 reader 继续看旧 generation。