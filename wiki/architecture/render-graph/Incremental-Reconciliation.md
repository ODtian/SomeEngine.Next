# Incremental Reconciliation

## Definition
结构编辑显式记录受触碰的 graph identities；commit 从这些 identity 开始更新，而不是重新扫描整张图寻找差异。

## Invariants
- structural storage 使用 copy-on-write 分块或等价持久数据结构。
- 未修改分块可被旧版与新版共同引用。
- resource→access、pass→access、pass predecessor/successor 等索引长期维护。
- 传播到结果不再变化的位置立即停止。

## Current direction
借鉴 retained UI 的 key identity、dirty propagation 与局部 reconcile，但不采用每帧重新生成树再做全量 diff 的方式。

## Relations
[[Incremental-Hazards]] · [[Incremental-Scheduling]] · [[Transient-Placement]]