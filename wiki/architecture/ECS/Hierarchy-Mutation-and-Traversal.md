# Mutation、destroy 与可选 traversal snapshot  Parent: [[Hierarchy]] 

typed `SetParent<D>`、`Detach<D>`、placement/order operation、policy conversion和`DestroySubtree<D>`提供需要完整lifecycle/placement语义的mutation surface；default-domain facade转发到同一实现。`Detach<D>`只移除Parent；该Entity随后是否为root由各workload candidate query决定，没有Join/Leave副作用。core reparent始终transform-agnostic，只改变Parent并保留现有LocalTransform。

任一domain的Parent只表达组织、transform或继承，不表达lifetime ownership：

- 普通 `Destroy(parent)` 以canonical Parent而不是可能stale的Children为准，把当时仍直接指向parent的surviving children detach，因此它们对相应workload成为roots；
- grandchildren继续挂在各自存活parent下，整个child subtree被保留；
- 同一stable command stream中已在Destroy前reparent走的child不受影响；
- 只有显式`DestroySubtree(root)`或显式Ownership Relation/policy才级联销毁；
- scene attachment、UI anchor、bone挂点等不会因为普通Parent关系意外获得所有权语义。

Entity destruction在同一structural transaction中清除该Entity拥有的各domain Parent、所有仍以它为canonical parent的direct-child Parent，以及相关Children/applied-parent/order状态；每个domain独立应用orphan规则。跨domain的相反parent方向不共同构成cycle，需要跨domain lifetime语义时使用显式Ownership Relation/policy。即使destroy前存在deferred freshness gap，事务也按canonical Parent收集children并通过共享transition kernel清理affected inverse，不能依赖stale Children漏掉orphan。

structural candidate interpreter与Deferred owner finalizer都维护dynamic forest。当前实现使用deterministic ancestor walk处理局部link/cut，并在owner结束或publication前对受影响canonical image运行完整forest validation，覆盖unique parent、self-parent、dead target与cycle。错误选择按stable entity/key确定，不依赖哪个worker先发现。当前没有按规模切换link-cut tree的隐藏threshold；若以后引入dynamic-tree specialization，必须先给出workload分布、grain公式与benchmark证据。

`Children<D>` component只是一枚受保护的inverse-presence token，不内嵌child collection。公开
`GetChildren` 返回 `HierarchyChildrenSnapshot<D>`：它通过 `ReadOnlyMemory<Entity>` 固定一份
已发布的直接children generation，后续maintenance发布替换数组而不修改旧数组。generation只
保护该point-in-time applied inverse，不是canonical hierarchy truth，也不阻止下一次
maintenance；fresh snapshot依赖maintenance handle，旧snapshot可以继续观察旧应用态。
需要整个workload的roots、adjacency和depth时，由propagation planning owner在自己的
scheduler-handoff边界捕获专用packet数据，不能把单parent snapshot冒充全图snapshot。

canonical Parent的multi-work-item写使用专用`TopologyPacketFinalizer<D>`，不冒充普通parallel query write。resource-bearing capture owner直接依赖semantic dependency，ready后取得topology/query storage Read，并从`Chunk.PersistentIdentity + StructureEpoch + TopologyRevision + full-tail row coverage`生成stable partition proof，把Entity与Parent复制到packet-local staging。公开range字段名是`PersistentChunkId/RowStart/RowCount/ChunkRowCount`；checked prefix offset只从proof推导，staging长度必须精确等于`TotalRowCount`。proof在线性构造时已经验证positive、从0连续、无gap/overlap、无A/B/A chunk重现和完整尾覆盖，因此finalizer不再重复支付O(packet²)交叉检查。

capture owner附着全部unmanaged packet children；这些packet只写互不重叠的staging ranges，不发布World，也不取得write/change tick。`TopologyPacketContext`只把`LastSystemVersion`作为capture query的selection baseline交给callback，刻意不提供`CurrentSystemVersion`，staging arrays同样不带version。全部packet结束后只保留无资源finalizer launcher。empty packet set直接完成；非空但`staged == preimage`的no-op也在登记Parent/topology writer之前完成，因此两者都不取tick、不发布revision。

只有changed image才让唯一serial finalizer取得Parent/topology Write。finalizer在这个writer admission之后验证live精确query membership与captured Parent preimages；无关topology revision推进可以合并，membership替换、preimage冲突或不同final image则稳定fault。验证通过后它创建structural candidate，并且只在candidate内取得一次commit version；所有changed Parent rows、chunk coarse watermarks、native journal entries以及同次forest maintenance产生的Children facts都使用该version，然后以一次root publication提交。packet fault、validation fault或forest fault都不会发布candidate，也不会把staging version误当成World commit version。

