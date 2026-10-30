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

资源的版本顺序分析共用当前顺序关系的祖先搜索结果。按实际查询的 pass 计算并保留
祖先位集，资源通过与自身使用者相交获得排序约束，随后按声明顺序处理同时就绪的
使用者。显式顺序、当帧顺序或声明位置约束的有效边变化，以及 pass 身份、启用、
声明位置、调度方式或行成员变化，使搜索结果在下一次依赖分析时统一刷新。
资源范围和内容变化继续复用已有顺序结果，顺序关系仍由原图数据持有。
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

## CPU command recording

Each submission retains its GPU pass order and completion dependencies. Within it, contiguous
passes with the same recording-thread requirement and command-reuse setting form recording
ranges. Worker-eligible ranges use summed callback and barrier recording estimates to form
balanced command lists, bounded by the current worker count plus the calling thread. The same
work estimate determines parallel recording and combines small ready ranges in `RecordAhead`.
The lists persist until submission membership, recording options, worker configuration or GPU
timing settings change.
When a changed submission retains a command list's pass members and recording settings, it
retains that list and its command-reuse state while updating the affected ranges.

Command-data, binding and resource changes invalidate the affected command list. Other lists
continue through `TryRepeatCommands`; an invalidated list uses its existing command context.
Calling-thread callbacks retain their calling thread and relative order while worker-eligible
lists can record ahead. Submission consumes the lists in GPU pass order, and every recording
job is joined before frame execution returns. The implementation is in
[FrameRecording](../../../src/SomeEngine.RenderGraph/FrameRecording.cs) and
[FrameRecordingAhead](../../../src/SomeEngine.RenderGraph/FrameRecordingAhead.cs).
