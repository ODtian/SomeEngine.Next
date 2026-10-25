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

## 值依赖更新

现行实现见 [FrameAnalysis.cs](../../../src/SomeEngine.RenderGraph/FrameAnalysis.cs)。显式修改先确定本轮脏资源和访问列表，并在协调阶段计算这些访问所需的 pass 顺序祖先。

每个脏资源使用独立、可复用的排序、区间和读写遍历工作区。共同 Job 执行器并行计算资源的值依赖边与活跃根；所有范围完成后，协调阶段归并各资源结果，集中更新依赖计数并继续既有活跃性传播。

正常帧沿 `TryReuseAnalysis` 复用已有结果；局部修改继续由资源和 pass 的变化集合确定重算范围。

## 执行冲突更新

值依赖归并和活跃性传播完成后，协调阶段固定本轮脏资源的活跃访问。资源冲突 Job 复用上述独立工作区，分别计算重叠读写和纹理布局变化产生的依赖边。

全部资源计算完成后，协调阶段比较各资源的新旧冲突边，集中更新执行边计数和受影响的调度数据。该阶段沿同一脏资源集合处理局部编辑，正常帧继续复用既有分析。

## Relations
[[Incremental-Hazards]] · [[Incremental-Scheduling]] · [[Transient-Placement]]
