# Hierarchy/transform  Parent: [[review/ECS-Validation|ECS Validation Checklist]] 

- self/2-cycle/long-cycle/dead parent/detach+reparent/destroy+reparent；
- ordinary parent Destroy只 orphan direct children并保留 grandchildren subtree；reparent-before-destroy存活；显式 DestroySubtree才级联；
- Immediate add/replace/remove在返回时Parent/Children/applied-parent一致；Deferred mutation只推进Parent，maintenance前relaxed Children保持last-applied，fresh reader通过显式handle依赖追平；
- immediate hook与deferred system对同一transition sequence产生相同Children、order和native change facts；Immediate后system no-op，不重复应用；
- generated `ref Parent<D>` / writable Span可并行处理owned rows但固定Deferred；success完成forest validation，body fault/cancel或非法self/dead/cycle恢复preimage且不暴露非法Parent；
- Parent多次Changed及Add/Remove折叠到一次maintenance时，internal applied-parent仍能准确清理old shard并应用final Parent；不存在public applied-parent或delta API；
- same-child stable sequential assignments和 ordered sibling冲突的稳定结果；
- 同一 graph不同 parent的 ordered/unordered混搭；policy不继承；unordered shard没有 per-child order metadata或稳定枚举承诺；
- unordered -> ordered显式 permutation与 stable-logical-id fallback、ordered -> unordered丢序、转换前后 generation pin隔离；
- unordered instrumentation始终报告零 order-key bytes、零 renumber和零 ordered inner-loop dispatch；普通 child view不能取得拓扑可写 span；ordered capability跨 policy conversion或 generation lifetime不能逃逸；
- topology reparent保持Local并重算World；显式keep-world使用fresh propagation dependency并原子发布Parent+Local；pass-through ancestor、detach-to-identity和non-invertible parent abort全部覆盖；
- default与多个typed hierarchy domains可共存；同一Entity在各domain的Parent与local order policy互不污染；destroy一次提交按canonical Parent orphan/清理全部domains；domain-qualified jobs无假冲突；unused domain零page/payload allocation；
- public model只有`Parent<D>` + read-only `Children<D>`；binary/API shape中不存在`HierarchyNode`、Join/Leave或core membership；不同workload candidate queries可让同一Without-Parent Entity在一个workload是root、另一个workload完全不参与；
- flat、wide、deep、many forests、minimal dirty roots、subtree reparent；
- transform bundle closure与workload-owned identity/pass-through organization entities；
- parent-before-child、siblings真正并行、无关 ranges不写；
- ordinary Children view job-safe且永远read-only；可选snapshot old-generation reader安全，fresh view/snapshot的语义由maintenance dependency决定；
- cross-frame playback -> propagation -> consumer只使用显式 semantic handles。

