# Transform / hierarchy propagation  [[architecture/ECS-Architecture|ECS Architecture]]

## GameWorld updates

`GameWorld` 默认通过 `WorldJobs` 使用共同调度器，显式传入的 `WorldExecution` 继续提供该 World 的执行关系。
`GameWorld.Update` 先完成游戏系统与结构回放，再完成变换更新，后继提取在这个返回边界读取结果。

[`TransformSystem`](../../../src/SomeEngine.Core/ECS/Systems/TransformSystem.cs) 先完成显式层级维护，
再通过生成式实体作业将变化的平坦实体按有效行范围写入 `WorldTransform`。
层级变换候选交给 [`ParallelTransformPropagation`](../../../src/SomeEngine.Core/ECS/ParallelTransformPropagation.cs)
统一归一化为互斥脏子树；传播沿同一维护令牌保持父先于子与组织节点透传规则。
协调线程等待传播句柄完成，行写入版本与变换值在同一更新返回边界可读。

公开行为入口为 [`TransformSystemTests`](../../../tests/SomeEngine.Core.Tests/ECS/TransformSystemTests.cs) 与
[`ParallelTransformPropagationTests`](../../../tests/SomeEngine.Core.Tests/ECS/ParallelTransformPropagationTests.cs)。

## Propagation access

generated access 完整声明：

```text
Parent<D> Read + fresh Children<D> Read
explicit dependency on hierarchy maintenance handle
optional workload-scoped HierarchySnapshot<D> capture + generation pin
World topology Read
LocalTransform exact range Read
WorldTransform boundary-ancestor ranges Read + affected subtree ranges Write
```

Transform propagation先显式依赖authority domain的hierarchy maintenance，再通过native `Added/Changed/Removed<Parent<D>>`、`Added/Changed/Removed<Children<D>>` query windows与LocalTransform dirty rows计算minimal dirty roots，不消费ParentDelta。roots来自Transform workload candidate query的`Without<Parent<D>>`，不是core membership。若一个dirty candidate已有dirty ancestor，则只保留ancestor。每个dirty root表示唯一subtree；这是并行写不alias的证明。用于本次propagation的dirty/affected scratch只属于该owner，不成为第二套持久topology truth。

执行采用显式root packet grain：normalized roots按stable Entity identity排序，`packetCount = ceil(disjointRootCount / rootsPerPacket)`；`rootsPerPacket`由调用方公开给出，默认1，不读取worker count、不使用隐藏规模threshold。resource-bearing planning owner直接依赖typed maintenance token并持有Parent/topology Read，从proof capture开始一直保持到它附着的全部data-only packet children结束；不存在会提前释放lease的外层launcher。planning以迭代DFS预捕获每个packet的parent-before-child traversal nodes，同时验证duplicate visit和Parent/Children双向一致，因此proof与packet执行之间不能插入topology writer。即使root集合为空，也先验证用户声明的完整query access set。proof fingerprint包含entity、canonical parent与depth，不能把具有相同DFS entity序列的flat tree和chain混为同一拓扑。不同packet拥有不相交root ranges/subtrees并由Job scheduler work-share；同一packet内parent严格先于child。每个user `Execute`都处于restricted World scope，普通World/query/hook/CommandBuffer/tick或other-root hierarchy调用在产生副作用前拒绝，唯一ECS入口是typed `HierarchyPropagationContext<D>`。whole-family write declaration只用于授权；capture随后把它编译成受影响node的coalesced stable-row Write ranges和subtree外ancestor的Read ranges，callback每次read/write再验证目标单行range。因此同一component family的断开subtree不会假冲突，同一行仍严格串行。每个component write只允许当前node；当同family也可写时，跨Entity read只允许canonical ancestor，另一root、sibling、descendant或任意lookup机械拒绝。packet启动前还会反解全部World storage declarations，拒绝managed/reference-bearing component、relationship write、buffer/sparse/topology冒充table access以及用户伪造的table range；因此unsupported alias不会在部分subtree已经写入后才暴露。parallel child完成resource admission后仍保持versionless；首个通过全部guard并真正调用`Write`的node才发布一个共享execution version，全部实际写入的row/coarse/journal facts使用该版本。read-only、unused-write、empty roots与rejected access不推进clock。每个WorldTransform因此只有一个writer，sibling completion order不影响结果。

transform closure固定为：Transform workload中的transform-bearing Entity必须通过bundle同时拥有 `LocalTransform + WorldTransform`；只拥有其一是registration/structural validation错误。被该workload candidate query纳入但两者都没有的组织Entity是identity/pass-through，descendants继承最近transformed ancestor（若不存在则identity）。因此scene hierarchy可以包含纯组织节点，同时parent-before-child proof仍有唯一输入，而不需要core `HierarchyNode` membership。

Transform层提供显式 keep-local与keep-world reparent operations，不使用含糊的bool默认值。keep-local复用core topology reparent；keep-world在显式依赖最新 propagation handle后读取child WorldTransform与new parent的effective WorldTransform（穿过identity/pass-through nodes，整条链无transform时为identity），求解新的LocalTransform，并在同一个prepared World root中原子发布Parent与LocalTransform。目标effective transform不可逆或结果不能由TransformQvvs精确表示时，整个compound transaction稳定失败，Parent与LocalTransform都不改变；不允许近似、NaN fallback或partial commit。UI/layout可在core hierarchy之上定义相同事务形状的domain-specific preserve-layout operation，但不把UI数学放进hierarchy内核。

owner ranges由dirty roots、fresh Children view、预捕获traversal metadata和stable chunk membership精确推导。当前实现不做whole-family Write fallback：coalesced interval数量是显式调度成本，但无关subtree不会为方便实现而失去并行度。若未来profiling证明需要不同表示，只能替换range index的物理结构，不能静默扩大逻辑owner范围。

