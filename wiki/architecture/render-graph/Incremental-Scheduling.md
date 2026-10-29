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

资源的版本顺序分析共用当前显式依赖的祖先搜索结果。每轮依赖分析按实际查询的
pass 计算祖先位集，资源通过与自身使用者相交获得排序约束，随后按声明顺序处理
同时就绪的使用者。搜索工作区随下一轮分析刷新，显式依赖仍由原图数据持有。
包含循环的关系沿原资源投影处理，资源版本及有效 pass 的循环检验保持原行为。

## Relations
[[Incremental-Reconciliation]] · [[Incremental-Hazards]]

## Compatible queues and submissions

Compatible compute passes are grouped through their compute dependencies. A group whose
fixed producer and consumer boundaries use one queue stays on that queue when its summed
estimated execution cost fits the boundary handoff cost. Each boundary direction contributes
two minimum execution-cost units for source and destination submission work. Other compute
groups compare the estimated queue start time, including a producer handoff. Producer and
consumer transfer counts break equal estimates. Compatible copies retain their
producer/consumer queue preference. Existing compute queues remain selected while they
satisfy the pass declaration and the group's boundary preference.
The implementation is in [FrameSchedule](../../../src/SomeEngine.RenderGraph/FrameSchedule.cs).

Initial submission construction and incremental submission updates split before a pass
that introduces another queue's wait for the existing prefix. The prefix can be submitted
with its own dependencies while the suffix waits for its producers. Adjacent submissions
merge under the existing wait and consumer-release constraints; swapchain image uses retain
their single-submit interval. See [FrameRecording](../../../src/SomeEngine.RenderGraph/FrameRecording.cs)
and [FrameSubmissions](../../../src/SomeEngine.RenderGraph/FrameSubmissions.cs).
