# Page Faults

Page fault 表示 traversal 遇到 nonresident Page ID 后产生的缺页请求。

## Invariants
- fault queue 是 fixed-capacity worklist。
- overflow 可以直接丢 fault；页仍保持 nonresident，未来 traversal 自然再次产生 fault。
- 不建立 guaranteed replay / acknowledge 协议。
- fault 与 [[Residency-Usage]] 是两套信息。
- fault readback 只影响未来 frame。

## CPU 页面需求收集

`PageStream` 将本轮叶请求交给 `ClusterMeshes.CollectPageRequests`，显式页面需求交给
`CollectPageDemands`。`ClusterMeshes` 沿原目录锁保持 BVH 叶索引、Mesh 页面来源和 LOD
group 邻接有效，使用 `JobSystem.ExecuteParallel` 同步完成范围收集。

每个范围持有自己的显式页面优先级、依赖页集合和叶映射结果。叶映射、页面去重与
LOD 依赖展开读取同一本轮目录；汇合先归并显式页面的最高优先级，再以全批最高优先级
补齐依赖页，保留显式页面原有优先级。

同步返回覆盖全部范围输出的完成。协调线程随后沿 `ResolvePageCore` 集中触碰驻留页、
撤回逐出，再由 `PageStream` 的原路径维护页面引用、预算、读取登记与异步装载。
目录和页面来源继续由 `ClusterMeshes` 持有，范围集合只承载本轮计算输出。
