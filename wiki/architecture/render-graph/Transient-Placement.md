# Transient Placement

## Definition
Transient 资源的内存位置具有跨局部结构修改的连续性。

## Invariants
- 未受影响的资源默认不移动。
- 新资源与尺寸变化资源优先利用已有空洞。
- 普通 commit 不以全局最紧凑为理由重排所有 transient 资源。

## Relations
[[Explicit-Compaction]] · [[Incremental-Reconciliation]]