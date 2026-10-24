# Native change tracking  Parent: [[Hierarchy]] 

hierarchy变化只通过普通ECS facts表达；当前由保守的system `LastSystemVersion`窗口消费，未来可在不改变facts的前提下接入per-consumer checkpoint：

```text
Added/Changed/Removed<Parent<D>>   canonical parent变化
Added/Changed/Removed<Children<D>> derived inverse应用变化
Added/Changed/Removed<local order/policy component> 局部顺序能力或顺序变化
```

`Removed<Parent<D>>`保留removed history所需的old value；maintenance同时使用internal applied-parent确定实际old inverse state。删除`HierarchyChanges`、`ParentDelta`、membership/order delta以及任何hierarchy-specific change-stream API。transition batch的affected-child/old-parent/new-parent scratch只属于当前owner，结束后丢弃，不能进入World/public API成为另一套delta。没有变化的maintenance通过native query window与applied-parent比较直接no-op，不要求每帧全表reconcile。

