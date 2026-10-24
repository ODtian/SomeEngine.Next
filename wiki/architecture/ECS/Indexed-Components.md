# Indexed component  Parent: [[Workload-Rules]] 

普通 raw writable Span不能同时维护 old/new keys。indexed type默认通过 `IndexedRefRW<T>` / indexed range wrapper记录 old/new；bulk adapter若必须给高吞吐写入，则在 body前 snapshot owned old keys，body后比较，或直接重建 affected index shards。body fault时 finalizer仍根据实际 dirty rows完成 index publication后再发布 WorkReleased；index必须反映 partial values。maintenance capacity在 body前准备，cleanup不得二次 allocation后留下 stale index。

