# Incremental Scheduling

## Definition
排序关系变化后只修复受影响的 DAG 区域。

## Invariants
- 从 predecessor/successor 发生变化的 pass 开始。
- 沿真正受影响的相邻节点传播。
- 已缓存结果与新结果一致时停止传播。
- 不因为一个局部 edge 变化重新排序整个 graph。

## Current design
具体动态拓扑算法可替换；本页只固定局部更新语义。

## Relations
[[Incremental-Reconciliation]] · [[Incremental-Hazards]]