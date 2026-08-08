# ECS Job、Relation 与 Hierarchy 并发模型

本页是 SomeEngine.Next 对 ECS job、安全借用、system 调度、结构事务、relation 和 hierarchy 的统一终态设计。它描述可依赖的语义，不描述临时迁移状态。

## 结论

SomeEngine.Next 使用四个相互独立的正确性概念：

1. **Semantic Dependency** 选择必须观察的前序结果，等待完整 scope，并传播 fault/cancel。
2. **Resource Owner** 保证已 admission 的访问不重叠，等待前序 work release，不传播业务 fault。
3. **Job Scope** 包含 body 和所有动态 descendants，决定 handle 何时真正完成。
4. **Lifetime Domain** 决定 runtime、World、system/module 何时可以停止接收 roots 并安全释放。

`Schedule`、`Execute` 和 external submission 是同一个 ResourceManager 的三类 owner：

```text
Schedule          async CPU owner
Execute           caller-thread synchronous owner
External submit   externally-completed owner
```

ECS 业务代码不能构造 World 的 raw `JobResourceAccess`。query、generated job、typed lookup 和 command writer 是唯一入口。

## 不变量

- 访问描述可以存储；真实 ref/span/view 不能在 owner 之外存在。
- 名称也是契约：所有导出的 `*View` 必须是 byref-like 的 callback-scope borrow；可以跨
  publication 保存、由 `ReadOnlyMemory<T>` 固定 immutable generation 的值必须命名为
  `*Snapshot`，不能伪装成 View。
- `*Desc` / `*Descriptor` 必须拥有多个已经正规化的独立事实及其验证规则；只保存一个
  被转发对象、没有自身不变量的 descriptor 是禁止的包装层。
- 产品代码中的 `ToArray` 只允许出现在显式 serialization plan、snapshot publication、
  immutable owner/descriptor construction、scheduler handoff、topology transaction 或
  SourceGen 边界；query row/chunk 枚举、component/buffer/sparse read/write 热路径不得物化。
- 显式依赖先完成，resource admission 后发生；未 ready 的 job 不预占资源。
- 一个 ECS query job 使用一个逻辑 owner和多个内部 work items；内部 batch 不互相进入 resource frontier，但必须先通过 per-packet self-alias proof。
- 所有可能被 entity destroy、component membership change、relocation 或 chunk retirement 失效的 ECS borrow 都隐式持有 World topology Read；只有 immutable generation pin 例外。结构事务持有 topology Exclusive。
- relation endpoints、Children、Depth、adjacency/index 等 derived 或受保护的不变量数据不能通过普通 RW ref 修改。`Parent<D>` 是例外：owner 内的 job-safe writable ref/span 是合法的 deferred hierarchy mutation，必须经过 domain access admission、change publication 和 owner 结束时的 forest validation。
- 结构事务在 live commit 前完成验证与分配，并原子发布它本次包含的 canonical state、derived state、journal 和 epoch。该原子性不消灭 hierarchy 的显式 deferred 语义：合法 Parent 可以先于 Children 可见，Children 在 maintenance 前表示 last-applied inverse。
- system callback 保持稳定顺序，负责编排并可进入显式 caller-owner Execute；它不裸访问 ECS，重工作由 job承担，也不存在 frame-global completion。
- 调度时序、worker 完成顺序和 dictionary 顺序都不是业务确定性来源。

## Job 状态与完成语义

```text
Created
  -> PinnedWaitingSemantic
  -> AdmittingFixed | PreparingTopology -> Sealing
  -> WaitingHazards
  -> Ready
  -> Running
  -> WorkReleasing
  -> WorkReleased
  -> WaitingDescendants
  -> ScopeCompleted
```

### WorkReleased

`WorkReleased` 表示该 owner 的 body、固定 parallel batches 或 external activity 已结束，资源访问和 pins 已释放。它不等待动态 children。

resource hazard 只等待 `WorkReleased`。这样父 body 释放资源后，无关 child 不会阻塞资源 successor。

### ScopeCompleted

`ScopeCompleted` 表示 `WorkReleased` 加所有 descendants 和 scope-owned resources 都已结束。public `JobHandle` 的显式 dependency 等待这个事件。

默认 `RequireSuccess` dependency 传播 fault/cancel；显式选择的 `AfterCompletion` dependency 只建立 scope 顺序，用于 retry、cleanup 和 checkpoint serialization。两者参加相同 cycle detection。单纯 resource predecessor 的 fault 不自动取消 successor。值写 job 不是事务，body fault 后可能留下部分值写入，因此需要有效结果的消费者必须使用默认 dependency 依赖该 handle。

## Readiness-order admission

一次异步提交执行以下顺序：

```text
normalize descriptors
  -> reserve ResourceState/container identity and acquire lifetime permits
  -> attach scope and register full semantic dependencies
  -> wait semantic ScopeCompleted
  -> atomically admit a fixed access set, or run the sealed ECS preparation protocol
  -> wait conflicting predecessor WorkReleased
  -> create views and execute
  -> release accesses/pins
  -> join descendants
```

这意味着先调用 `Schedule` 但仍等待 dependency 的 D，可以被后提交且已经 ready 的 X 超越。resource access 的契约是“不重叠”，不是“后 Schedule 的一定读到先 Schedule 的结果”。需要读新结果时必须传显式 `JobHandle`。

owner 进入 ready-admission queue 后按 ready enqueue sequence获得公平服务；同时 ready 的稳定 coordinator submissions以 logical job id打破平局。公平性防止 dependency 刚完成的 owner 被持续新提交饿死，但不把原始 Schedule 顺序伪装成 freshness。

如果在 Schedule 时把尚未 ready 的 access 预先放入 conflict frontier，动态 scope 会产生不可消除的环：父 P 持有 R，D 占据 R 且依赖 P，P 创建的 child C 需要 R，随后另一个依赖链又等待 D。readiness admission 删除的是未 ready owner 的 conflict ownership，因此 resource edges 只在可执行 owner 之间建立；submission 仍保留不参与冲突的 identity/lifetime reservation，防止等待 dependency 时 `ReleaseResource` 复用同一 identity。dependency success 才在一个锁内针对当时 frontier 激活固定 access set，dependency fault/cancel则释放 reservation。

固定 access set 的多资源 admission 在一个线性化事务中完成；动态 ECS query 使用下节定义的 prepare/seal 等价协议。frontier 使用 whole-access frontier 加 augmented interval index，range 查询目标复杂度为 `O(log N + K)`。

access normalizer 构造 piecewise mode map，而不是无条件让 whole 吞掉 ranges：whole Write 可覆盖所有 ranges；whole Read 只覆盖 range Reads；whole Read + subrange Write 必须保留为 default Read 加 Write overrides，或显式保守提升到 whole Write。任意重叠段取最强 mode，相邻同 mode 段合并。

semantic graph 仍要做 cycle detection。child 直接或间接依赖当前/ancestor scope 必须拒绝，`CombineDependencies` 不能丢失 prerequisite graph。任何 job/Execute body 内的 `Complete` 和嵌套 `Execute` 都默认拒绝；需要嵌套 helper 时复用 ambient owner，并验证请求只是现有权限的子集。

pin 使用强 container anchor、distinct ResourceState references 和 generation。`ReleaseResource` 在所有 safety modes 都不得释放或复用 active/pinned resource；Fast 只能减少诊断，不能把 pending access 变成 use-after-free。

## 同步 Execute

`Execute` 不是“先 Complete conflicts，再裸调用 body”。它自己是 owner：

```text
pin -> semantic wait -> atomic admission -> hazard wait
    -> caller-thread body -> WorkReleased -> descendants join
```

admission 是线性化点：先存在的冲突 owner 是 predecessors，后提交的冲突 owner 会看到 Execute owner 并成为 successors，因此 wait 返回和 body 开始之间不存在 schedule-after-wait race。

等待使用 cooperative pumping；zero-worker runtime 也能前进。小 workload 使用相同 Execute 语义。managed/thread-affine workload 还必须声明目标 executor 和真实 alias resource；“当前 caller”本身不构成线程或对象安全证明。

## ECS admission 与 range

所有 ECS owner 通过 per-World preparation sequencer。query 的匹配 chunks 只有在 topology 稳定后才能知道，因此 ECS admission 是一个受约束、可回滚的 prepare/seal protocol：

```text
semantic ready and pins held
  -> sort all Worlds by stable WorldId + generation
  -> acquire their preparation sequencers in that order
  -> atomically register topology guards for every World
  -> wait prior topology writers without holding ResourceManager lock
  -> resolve cached queries against stable topology
  -> derive stable chunk/range sets
  -> atomically seal every remaining ECS/external access
  -> publish owner as admitted and release sequencers
  -> wait data hazards
```

sequencer 只串行 metadata preparation；已运行 work 和 release 不被它阻塞。所有 ECS 入口都经过该 sequencer，后来的 owner 不能在 topology guard 和 range seal 之间插入并制造升级环。resolve、cancel、World Closing 或 seal failure 在同一 cleanup path 撤销 topology guards、sealed accesses、pins 和 domain permits，且 owner从未发布 Ready。

topology guards 和最终 range set 属于同一个 Preparing owner；对其他提交的线性化点是成功 seal。ResourceManager lock 只用于短注册/撤销，固定获取顺序为 sorted domain permits -> sorted World sequencers -> ResourceManager admission lock，等待 predecessor 时绝不持 ResourceManager lock。

`ComponentLookupView<T>`、dense/sparse/indexed component、dynamic-buffer header/payload、relation-edge payload、entity existence/location 和 chunk/meta view 全部隐式带 topology Read。snapshot-only borrow 不读取 live storage，使用 generation capture/pin，因此不进入这条 sequencer。resource-bearing external submission 只要引用 ECS storage，也必须走同一 protocol。

range identity 使用 World-qualified、全局单调且永不复用的 64-bit `Chunk.PersistentIdentity`，fork 保持 identity，capture 的 `StructureEpoch` 拒绝 stale packet；未来 chunk pooling 也必须分配新 identity，不能复用旧 id。它不能使用会因 archetype compaction 改变的 index。默认 query owner 拥有 whole matching chunks；只有生成器或专用 partitioner 能证明不相交时才使用 row ranges。

一个 `ScheduleParallel` query 对全部匹配 ranges admission 一次，然后把固定 stable batches 交给多个 work items。`WorkReleased` 在所有 batches 结束后发布。batch 是内部执行单位，不是 child owner；安全性来自生成的 partition proof，而不是“同 owner 自动安全”。

### Owner 内 packet alias proof

SG 为每个 parallel adapter建立独立于 ResourceManager 的 alias matrix：

- current-row/current-chunk direct access 可由 stable packet partition证明唯一；
- 某 family 有任一 direct packet Write 时，同 family 的 indirect whole/shard Read 或 Write 默认拒绝；
- indirect Write 只有 target-to-packet mapping 可机械验证、且所有 aliases落在同一 packet时允许；
- direct signature 中同 family 的 RO/RW重复 alias拒绝，而不是给用户两个指向同一元素的参数；
- immutable snapshot 是不同 resource identity，可用于 read-old/write-new two-pass；
- atomic、reduction、append 使用各自认证的 `ParallelWriter`；
- hierarchy parent-before-child ready-frontier 是专用 internal proof，不能泛化给普通 query。

因此 `ref Position current + ComponentLookupView<Position> all` 即使 lookup 只读，也不能 `ScheduleParallel`；用户改为 snapshot、two-pass、单线程 `Schedule/Execute` 或有证书的 partitioned lookup。

## 可存储描述与临时 borrow

### 可存储

- `QueryDefinition` / `QueryHandle`
- `ComponentLookupHandle<T>`
- generated access descriptor
- `HierarchyChildrenSnapshot<D>`（固定一份已发布的 immutable child generation）
- `SystemChangeCheckpoints`（仅限 `SystemGroup` 同步 update callback 的成功提交范围）
- future asynchronous `QueryCheckpointHandle`（跨 Job scope 的 terminal lane 当前未实现）

它们只描述未来访问，不包含可解引用的 storage capability。handle 必须携带并验证 World/runtime generation。

manual synchronous shape使用现有`QueryHandle`与runtime-owned callback，不保留`RunQuery(lastSystemVersion, currentSystemVersion)`，也不让caller提供current version：

```csharp
QueryHandle moving = world.Query(
    world.QueryDefinition()
        .Read<Velocity>()
        .Write<Position>());

world.ExecuteQuery(moving, lastSystemVersion, cursor =>
{
    foreach (QueryRow row in cursor.Rows)
        row.ReadWrite<Position>().Value += row.Read<Velocity>().Value;
});

world.ExecuteQuery(moving, lastSystemVersion, cursor =>
{
    foreach (QueryChunkView chunk in cursor.Chunks)
    {
        ReadOnlySpan<Velocity> velocity = chunk.Read<Velocity>();
        Span<Position> position = chunk.ReadWrite<Position>();
        for (int i = 0; i < chunk.Count; i++)
            position[i].Value += velocity[i].Value;
    }
});
```

`QueryCursor`、`Rows/Chunks` enumerator、`QueryRow`、`QueryChunkView`与其中的span/ref都只在callback owner内创建；callback返回后没有release capability或borrow可逃逸。query declaration之外的component访问拒绝。具体tuple sugar可由SG添加，但正确性不依赖call-site rewriting。

### owner 活跃时创建

- `RefRO<T>` / `RefRW<T>`
- checked borrowed row/range wrappers
- generated callback 内的 `ReadOnlySpan<T>` / `Span<T>`
- `DynamicBuffer<T>` / `BufferView<T>`
- `ComponentLookupView<T>`
- relation adjacency borrow
- callback 内的 hierarchy current-generation traversal borrow
- `QueryRow` / `QueryChunkView`

这些类型形成完整 `ref struct` 链，并携带 owner id、borrow epoch 和 storage/snapshot generation。foreach convenience path 的 wrapper 每次 dereference 都执行不可关闭的 active-owner check；Fast 只可在 runtime 控制的 generated callback boundary 内把重复检查 hoist/elide，不能删除 admission、pins、generation lifetime 或 hazard ordering。

C# 的 `ref struct` 不是 affine/linear type：它仍可复制，且无法在已经抽出的裸 ref/span 每次使用时重新检查。因此强安全、高性能入口是 runtime 调用的 generated `Execute` callback；runtime 自己持有并在 `finally` 释放 owner，用户拿不到 release capability，raw spans 只作为这个 callback 的 `scoped` 参数存在。

当前`QueryChunkView`、`QueryRow`与child row cursor均为`ref struct`或只在其ref-struct链内可达。同步callback由runtime在`finally`中释放owner；raw Span只在callback词法生存期内出现，不能由普通public enumerable在owner外重新取得。

`HierarchyChildrenSnapshot<D>` 是刻意的例外而不是 borrow：它只持有
`ReadOnlyMemory<Entity>` 和 generation，publication 以后旧数组不再修改，所以该值可以存储。
它暴露的 `ReadOnlySpan<Entity>` 仍不能脱离持有 snapshot 的词法使用；需要独立数组所有权时
必须显式调用 `ToArray()`。旧名 `HierarchyChildrenView<D>` 已删除，防止调用者把可存储
generation 错认成 callback-scope capability。

copy-returning `World.Read<T>` 和 value/structural setter 可以是短同步 Execute boundary。独立 `World.Get<T>() -> ref T` 不能在 owner 外自动安全释放，因此只允许 ambient owner 内部使用或由 borrow view 替代。

同步 `Rows/Chunks` 枚举本身也是 Execute scope：正常结束、break 或 exception 时先释放枚举 body 的 resource owner，再 join 枚举期间创建的 descendants。共享 owner state 令 Dispose 幂等，但复制后的另一个 enumerator不能继续使用；它的下一次 MoveNext/wrapper dereference在所有 modes 拒绝。无法接受 analyzer约束的调用方必须使用 generated `Execute`，不能把 foreach 当作裸 borrow primitive。

## Generated job API

entity-oriented API 采用生态已经验证的 `IJobEntity`、`Execute(in/ref)`、`Schedule`、`ScheduleParallel`：

```csharp
public partial struct IntegrateJob : IJobEntity
{
    public void Execute(Entity entity, in Velocity velocity, ref Position position)
    {
        position.Value += velocity.Value;
    }
}

JobHandle integrated =
    new IntegrateJob().ScheduleParallel(world, dependency);

new ConsumeJob().Execute(world, integrated);
```

SG 从签名推导 query 和 RO/RW，生成 cache、access set、owner admission、packet alias proof、chunk adapter、batch dispatch 和 diagnostics。job struct 在 Schedule 时按值捕获，每个 packet得到独立 value copy；SG 拒绝依赖 job instance field回写结果。reference fields仍然 alias，必须声明 external resource或使用认证容器/reduction。

chunk/SIMD API 扩展现有 `IJobChunk`，由 SG 生成显式 adapter：

```csharp
public partial struct IntegrateChunks : IJobChunk
{
    public void Execute(ReadOnlySpan<Velocity> velocity, Span<Position> position)
    {
    }
}
```

普通 Roslyn generator 只能 additive，不能重写用户方法或 arbitrary call site。因此同步 query 由 runtime `ref struct` enumerator 自动持有 owner，不能承诺复制 Unity 的 `SystemAPI.Query` call-site rewriting。

SourceGen 必须作为 analyzer build-transitive 地进入每个 consumer compilation。generated code 位于用户 assembly，所需 runtime plumbing 使用 public + `EditorBrowsable(Never)` 的窄入口和不可伪造 runtime token；Checked/Strict 交叉验证 descriptor、World generation、owner 和 epoch。

### Random lookup

可存储的 `ComponentLookupHandle<T>` 只声明未来访问；owner 生效后生成 `ComponentLookupView<T>`。只读 view 登记 topology Read + whole-family Read；可写 view 登记 topology Read + whole-family Write，并默认禁止 `ScheduleParallel`。即使 view 只读，只要同一 parallel owner写 T，也会被 packet alias matrix拒绝。不提供通用 “disable parallel restriction” 属性；用户改用当前 row 的 `ref T`、immutable snapshot、two-pass、可证明的不相交 partition、容器专用 `ParallelWriter`，或未来经认证的`CommandBuffer.ParallelWriter`。当前普通共享CommandBuffer在Job内一律拒绝。

### 特殊不变量数据

SG 将 `ref Parent<D>` 或 Parent writable Span 识别为 deferred hierarchy write，而不是普通无约束 component write。generated adapter 为整个逻辑 owner 声明对应 domain 的 Parent read/write 与 validation/finalization access，在 body 前保存其 owned rows 的 preimage，在全部 packets结束后验证最终 one-parent、liveness、self-parent和acyclic forest；body fault、cancel或验证失败时在释放 owner前恢复 preimage并 fault/cancel handle。不同 typed domains可并行；同一 domain 的多个 deferred writers只有在同一逻辑 owner内合并，或 SG 能证明其最终 forest validation互不影响时才并行。

这种 ref/span 只改变 canonical Parent并发布普通 change epoch，不在 worker body 内随机写 old/new parent 的 Children。其 inverse transition由后续 hierarchy maintenance system批量应用。Parent component的结构性 add/remove和 placement-capable操作仍使用 typed API/command writer，以便记录 absence、old value和精确 sibling placement。

SG 和 runtime 继续拒绝：

- writable Children/Depth/HierarchyLink；
- writable relation source/target；
- parallel writable random lookup 未带可证明 partition；
- worker job 中不支持的 managed/thread-affine access。

这些 derived/protected 修改通过 ECS 内部 maintenance 或 typed hierarchy/relation command writer完成；用户永远拿不到 writable Children 或 relation endpoint ref/span。

## Change tracking

时钟窗口与写入版本是两个不同概念。`AcquireSystemTick()`在推进clock的同时返回推进前的baseline，供consumer保存“上次观察到哪里”；`AcquireSystemVersion()`返回推进后的新version，供本次写入发布row/coarse/journal facts。公开`ExecuteQuery`不再允许caller传入current version：writable query在exact query admission之后自动取得新write version，read-only query直接使用当前tick而不推进clock；显式current overload只保留给已经拥有精确owner的runtime-internal adapter。

对于直接写World storage的普通data owner，write/change epoch不在Schedule时更新。generated `IJobEntity`先完成完整owner admission与chunk/row filters，只有存在matching row且immutable descriptor含direct World write时才惰性取得一个共享version；empty、fully filtered和matching read-only执行都不推进clock。每个table/buffer/sparse写入复用该version，wrap-aware coarse publication防止较旧packet晚完成时倒退chunk watermark。hierarchy propagation更严格：packet入口与context创建都不取version，只有首个实际`context.Write`在relationship、capability、component presence与exact stable-row owner检查全部通过后，才通过internal trusted primitive取得一次共享version；所有packets复用它。read-only callback、声明Write但未调用、empty normalized roots和在guard处拒绝的访问均不产生假epoch。

topology packet是明确的例外，因为它的packet body只改detached staging而不写World。`TopologyPacketContext`只暴露用于selection/filter的`LastSystemVersion`，不分配或暴露`CurrentSystemVersion`，staging arrays也不携带commit version。唯一的topology commit version属于后续serial finalizer：它先取得Parent/topology writer admission，在structural candidate内调用`AcquireSystemVersion()`取得新write version，再把同一version用于本次Parent/Children的row、coarse与journal facts。empty capture和staged no-op均不调度World writer、不取tick，也不发布假topology revision。

当前`SystemGroup`除保存保守的system `LastSystemVersion`外，还为同步update callback提供
`SystemChangeCheckpoints`：checkpoint以World identity + `QueryHandle`区分，成功的query执行先
stage精确可见version，只有整个system update成功才提交；query或后续system body fault时stage
全部丢弃，下一次回退到上次成功checkpoint或保守baseline。因此，同一同步callback里的多个
consumer已经不必共享一条伪全局游标。这个实现不跨越异步Job scope，也不提供terminal lane、
`ScopeCompleted`提交或copied-handle identity语义。

若后续需要把上述同步checkpoint扩展为跨异步Job scope的per-consumer
`QueryCheckpointHandle`，必须一次性满足以下契约，而不能只把system结束时的全局tick写回Last：

1. 先以完整 structural match ranges admission；Changed 不能用于提前缩窄 owner ranges；
2. 等所有相关 component/enablement/topology hazards完成，此时 later conflicting writers已被当前 owner挡住；
3. 捕获相关 storage 的 completed-write upper watermark并在 owner内过滤 `(last successful, upper]`；
4. 整个 `ScopeCompleted` 成功后提交 checkpoint；
5. fault/cancel 不推进 checkpoint，下一次是 at-least-once retry。

届时checkpoint应是runtime registry中带identity/generation的`QueryCheckpointHandle`，复制handle仍指向同一lane。每次使用在lane内原子reserve sequence、读取previous tail并添加`AfterCompletion(previous)`，再发布新tail；上一次fault不传播，但checkpoint未推进，下一次重复处理。在system callback内，generated adapter可用ambient `SystemInstanceId + adapter id`解析persistent lane；同一system里需要多个同-type lanes时用户显式提供不同handle。脱离generated system的manual caller也必须显式传handle，SG不能假装从任意call site/副本推断identity。这是checkpoint私有terminal order，不是`GlobalDependency`。

该未来checkpoint lane必须是coordinator/root的顺序状态，不允许从同一active scope的dynamic descendant重入，也不允许新增lane edge后回到current/previous tail scope；它与普通semantic edge一起做cycle detection并在Schedule时确定性拒绝，而不是等待成死锁。diagnostic应同时报告checkpoint id、previous owner、current scope和闭环ancestor edge。dynamic branches使用不同checkpoint handles，或由coordinator在scope完成后再次使用同一K。这样才能保留schedule-order、fault retry和不漏change，而不把checkpoint降成会overtaking的resource hazard。

upper 不是“最大已发号 writer”这种会被 out-of-order completion跳过的全局 max；query在 hazards完成后读取各 matching chunk/shard的 stable completed versions，并用捕获的 epoch fence限定本次窗口。structural membership、enablement和chunk generation publication也取得 epoch并参加对应 filter watermark。

## System

system callbacks 按显式 order 和 registration tie-break 在 coordinator thread 上顺序调用。callback 负责生命周期、读取轻量配置、提交 jobs，并可调用显式 `Execute` 进入 caller owner；它不能在 owner外取得 ECS ref/span。job bodies 按真实 resource/range 并行。

system order 只表达 callback/lifecycle 顺序，不暗示数据 freshness。需要 B 读取 A 的新结果时，B 的 job显式依赖 A 的 handle；紧密 job pipeline 应在同一 feature/system 中组合，generated compound pipeline 可自动传递精确 handle。

删除以下正确性机制：

- `SystemContext.GlobalDependency`；
- frame-start reset 和 frame-end `Complete`；
- `SystemAccessManifest`；
- `SystemScheduleBuilder` 的 read/write stages；
- `RequiresBarrierAfter`、public barrier 和 `CompleteConflicts`。

disable 阻止未来 update submission。remove 先关闭 system Lifetime Domain，等待该 system 已提交 roots 的 `ScopeCompleted`，然后调用 `OnDestroy`。World/runtime 使用相同 close protocol。

## Lifetime Domain 与 runtime

domain 状态为：

```text
Open -> Closing -> Closed
```

root scope 在 Open 时取得 permit，并持有到完整 `ScopeCompleted`；descendant 继承 permit。Closing 拒绝新 roots，但允许已有 scope 继续产生 descendants。当前同步 `Dispose()` 等 tracked job root scopes 与 unbound admissions 全部结束后才释放 storage。

一个 job 可同时持有 runtime、World、system 和 module permits。访问另一个 World 需要取得另一个 domain permit；该 World 已 Closing 时 submission 失败。

多 domain/World acquire 和 close 一律按 stable `(domain kind, WorldId/systemId, generation)` 排序；失败时逆序撤销已取得 permits/sequencers。两个跨 World jobs 即使分别以 A/B 和 B/A 声明，runtime也按同一排序准备，不能形成 sequencer deadlock。

runtime hot replacement 不能把 old generation handle 当作 completed。ambient job submissions 路由到 owning scheduler；handle operations 按 generation registry 找 owning runtime；cross-generation dependency 拒绝。旧 runtime 完整 Closed 后才能销毁 workers/resource manager。

当前不提供可取消的 `DisposeAsync`。若未来增加取消等待，取消也只能停止 caller 的等待并报告 outstanding owners，不能强制释放仍被 running body 使用的内存；World 必须保持 Closing。

持有某 domain permit 的 ambient scope 不能同步 close-and-wait 同一 domain，否则它会等待自身。该调用立即拒绝；scope内只能向 lifecycle coordinator请求 close，真正 await 必须在 scope外。system remove只由 coordinator发起。World A job可以关闭未持有 permit 的 World B；一旦它也持有 B permit，同样拒绝同步等待。

## External work

已有 fence 只能创建无资源 completion bridge。把 resource access 附加到已经开始的 GPU/IO work 无法证明安全，因此 resource-bearing external API 必须是 submission transaction：

```text
pin and semantic wait
  -> admit external owner
  -> wait prior hazards
  -> invoke provider.Submit on required thread
  -> atomically bind returned fence
  -> release owner only on signal/fault/device-lost
```

submit 抛错必须释放 owner/pins并 fault handle。external fault 不阻止纯 hazard successors，但显式 successors 收到 fault。shutdown 遇到永不 signal 的 fence 保持 Closing 并报告 fence/resource/owner，不能提前释放。

## 结构事务与 hierarchy maintenance timing

entity create/destroy、component add/remove、shared relocation、relation endpoint change、Parent component add/remove 和 command playback 使用同一结构事务：

```text
close producer streams
  -> stable merge
  -> allocate deferred entity mapping
  -> acquire topology Exclusive
  -> exact-clone published WorldStructureRoot into a detached candidate
  -> replay the stable stream exactly once inside the candidate
  -> validate each command and final invariants
  -> prepare hook-command overlay publication
  -> atomic WorldStructurePublication(root + epoch) swap
  -> retire old root and dispatch isolated cleanup/observers
```

Parent 的 ordinary value replacement还有一条受 owner保护的非结构路径：generated job的 `ref Parent<D>` / writable Span直接写 canonical component row，并在 owner finalization验证最终 forest。它不要求把 value write伪装成 command或 archetype transaction，也不会在 worker packet中偷偷写任意 Children shard。

Parent mutation的 transport与 inverse maintenance timing正交。同步 API 和 CommandBuffer playback在命令真正 apply时都可以选择：

```text
Immediate
  -> apply/validate canonical Parent
  -> internal relationship lifecycle hook
  -> shared parent-transition kernel updates Children + applied-parent
  -> return with Parent/Children consistent

Deferred
  -> apply/validate canonical Parent and publish native change facts
  -> leave Children/applied-parent at last-applied state
  -> hierarchy maintenance system later batches the same transition kernel
```

relationship hook是静态注册、由 ECS 拥有写权限的 invariant maintenance，不是公开任意 delegate，也不运行 layout、mount、render等业务代码。业务反应继续由普通 systems消费 ECS-native change query。CommandBuffer只决定 mutation何时 apply，不能暗中决定 immediate/deferred。

stable command key 为：

```text
(stable pipeline key, logical producer/batch path, producer-local sequence)
```

不能使用 worker id、JobId、admission order 或 completion timing。stable pipeline key由 coordinator或 caller显式提供；deterministic mode拒绝没有 stable key 的并发外部 producers。query producer path使用 stable chunk/partition id。

所有命令在 detached candidate root 上严格按 stable total order解释，不混用 whole-batch last-wins和 imperative semantics，也不存在验证 shadow 后再对 live World 二次回放：

- `SetParent/Detach` 立刻更新 candidate Parent并验证当前 forest；later assignment只有在此前命令都合法时自然 later-wins。命令携带的 maintenance timing只决定本次是否同时运行 inverse transition；
- `Reorder` 作用于该 key 时的 current candidate parent，detached child上调用是错误；
- `Destroy` 写入 tombstone；后续 targeting command稳定报错，当前 direct children按 policy成为 roots；
- `DestroySubtree` 销毁该 key 时 candidate hierarchy中的 subtree；此前已 reparent离开的 child存活，此后不能再“救回”tombstoned entity；
- deferred create -> attach -> destroy遵循同一顺序；destroyed relation target的 later retarget报错；
- component add/remove/replace、relation create/delete/retarget也按同一 candidate sequence执行；
- 每条 hierarchy命令保持 candidate canonical Parent forest无环，结尾再运行完整 uniqueness、liveness和cycle validation；Immediate命令还验证本次 Children/applied-parent transition一致，Deferred命令允许其在 maintenance前保持旧应用态。

因此 `SetParent(child,A) -> DestroySubtree(A) -> SetParent(child,B)` 的唯一结果是：第二条销毁 child，第三条是 targeting tombstone error，整个 live transaction不提交。

parallel dynamic child 的 producer path由 `(parent logical path, parent stable batch id, parent-local spawn ordinal)` 推导，不能由哪个 worker先调用 Schedule 决定。普通模式允许无 stable key 的外部并发 recording，但只保证内存安全并明确不保证 byte-for-byte playback order。

stable merge 与不接触 World 的预处理可以在 admission 前运行；读取 canonical World、克隆 candidate、回放、验证和准备 next publication 必须在 topology Exclusive owner 生效后基于稳定 epoch完成。允许 admission 前生成 optimistic metadata，但 exclusive 生效后 epoch 不符必须丢弃并重建。

`WorldStructureRoot` 包含 entity allocation/location、archetype/chunk topology、buffer overflow、sparse/shared membership、relation generations、已应用 hierarchy derived state、derived indexes、query registry、journal、clock和allocator/free-list image；publication epoch与root identity一起保存在`WorldStructurePublication`。一次结构 transaction通过一条 atomic publication reference swap发布它实际准备的全部内容，不暴露 transaction内部的半提交步骤；但 Deferred Parent mutation有意只把 canonical Parent推进到新版本，Children仍属于上一 applied version，二者的 freshness差异是公开语义而非 torn commit。

当前 entity-location image 使用固定 256-record persistent pages；detached root只复制page-reference table，第一次记录写才复制并按`Archetype/Chunk.PersistentIdentity`重映射该page。archetype/chunk shell保持candidate-private，chunk的entity/column/version/enable-mask backing在fork时只读共享，第一次chunk写一次性detach。DynamicBuffer overflow不随chunk backing深拷贝：header携带per-row owner identity，fork/move/promotion继续共享只读array，只有目标row第一次真实内容写才保留capacity并只复制`[0, Count)`；inactive capacity保持`default`，managed overflow对应的inactive inline storage保持清空。每个shared bucket只有一个不暴露mutable array的canonical immutable shared-component tuple，同bucket chunks与detached forks复用它。其余mutable owners仍使用exact detached clone；已发布的immutable Children、relation adjacency与index bucket generations继续安全共享。后续把sparse/index/shared/relation/hierarchy/journal等owner改成COW只能改变prepare成本，不能改变单次root+epoch publication、失败时old root不变、或callback可见语义。

任意 user hash/equality/copy、dictionary construction、allocation和 invariant validation 都在 swap前运行；失败只 Abort prepared root，old root未变。进入 swap critical section后 cancellation延后处理，publish path不调用用户代码、不分配、不 dispose，且只有不可失败的 root swap和预构造 bookkeeping。结构 participant必须满足 `Prepare / PublishNoThrow / AbortNoThrow` 契约，否则类型/provider不能注册为 structural participant。

removed managed values和潜在抛错 cleanup在 swap后隔离执行；old root/资源在 cleanup结束前保持强引用且不复用。cleanup/observer fault被报告但不回滚已完整发布的新 root。prepared-root invariant若在 swap前失败则 abort；若 runtime检测到根已损坏这种理论不可达状态，World进入 `Poisoned` 并拒绝新 access，不能只 fault owner后继续暴露部分状态。

Job-side structural production已经通过独立的`JobCommandBuffer`公开；它不是普通
`CommandBuffer`的可逃逸`ParallelWriter`。`IJobCommandProducer`与
`IJobParallelCommandProducer`只在callback内获得`JobCommandWriter`，固定producer key对应
私有segment并决定稳定merge顺序。所有Job对`World.Commands()`、预构造普通
`CommandBuffer`、Playback、Clear和Dispose的访问仍在首个side effect前拒绝，即使该Job声明
topology Write也不例外。

`JobCommandBuffer`为每个producer key提供私有command segment。`CreateEntity`返回segment-scoped deferred identity，playback才按stable merge顺序分配真实Entity。buffer单次消费，producer callback结束即seal segment，用户不能取得或共享底层普通`CommandBuffer`。

`JobCommandBuffer`在调度producer时注册其完整scope。playback finalizer对所有producers建立成功依赖；任一producer或descendant fault使整个buffer失败，所有partial segments丢弃、deferred identities失效且不允许partial playback。cleanup等待producer scope终止后回收；producers仍活跃时不能提前释放segments。

`ExecuteBundleSpawn/Add/Replace/SpawnBatch`复用同一candidate-root transaction。`BundleWriteView`是`ref struct`，World-touching操作验证active token、owner thread、descriptor、重复写和required writes。batch每次只租一个runtime并复用当前chunk，但每行保持lazy materialization：callback开始时未来行尚未进入`Chunk.Count`，因此hook/query/index backfill看不到future default rows。callback捕获World后不能直接写table/buffer/sparse/shared/topology storage；它必须通过view写声明值，或通过`World.Commands()`记录next wave。显式`AcquireSystemTick`与journal suppression control仍可使用，每次view write读取当时的tick/抑制状态。若当前materialized row的indexed component尚未写入，lazy index backfill明确拒绝，避免default key永久残留。

同步user component hook通过`DeferredWorld`读取当前ECS状态；通过闭包捕获原始World后的table/shared/topology写拒绝，component-local fast path不能绕过。runtime-owned direct buffer/sparse borrow可复用当前topology writer；若hook运行在Job中，仍必须由当前single-work-item scope精确声明对应storage capability。结构和值反应通过本次hook的`DeferredWorld.Commands()`取得thread/epoch-scoped、record-only `DeferredCommandWriter`，稳定进入next wave；writer离开回调即失效。hook外部副作用与callback的`ref TState`不是candidate root的一部分，失败时不会回滚；从未发布transaction复制到外部的raw Entity也没有post-fault有效性保证，必须丢弃。

## Relation

普通 typed relation 的 canonical truth 是 edge entity：

```text
edge entity
  RelationSource
  RelationTarget
  typed payload/marker T
```

每条 edge 都有独立 stable identity，并以 `RelationEdge<T>` typed handle作为single-edge operation identity。relation type静态声明 `RelationCardinality`：`Parallel`、`UniquePair`、`UniqueSource`、`UniqueTarget`或`OneToOne`，并静态声明`RelationDirection.Directed/Undirected`。这些policy只存在于type registration/schema，不逐edge存储；实现只为已声明约束维护必要indexes。collision与违反约束的retarget使整个结构事务失败，不upsert、不merge、不覆盖现有edge。

directed relation公开有角色的Source/Target与outgoing/incoming adjacency。undirected relation只保存一条canonical edge，公开固定的EndpointA/EndpointB slots以及两端incident adjacency；它绝不物化A->B与B->A两条truth。slots保持create/retarget给定的关联，使payload可保存LocalAnchorA/B等endpoint-local data；unordered-pair uniqueness把`{A,B}`与`{B,A}`视为同一key，但不偷偷交换slots或任意用户payload。payload端点具有施加者/接受者等非对称角色时必须声明directed。

undirected允许`Parallel`、unordered-pair `UniquePair`和表示每个endpoint incident degree至多一的`OneToOne`；`UniqueSource/UniqueTarget`在registration时拒绝。generic relation默认允许self-edge，relation declaration可静态禁止；Hierarchy Parent仍始终禁止self-parent。undirected self-edge只在该endpoint的incident view出现一次，歧义的single-endpoint replacement被拒绝，caller使用whole-edge retarget。

public topology surface固定为：

```text
CreateRelation<T>(source, target, payload) -> RelationEdge<T>
DestroyRelation(RelationEdge<T>)
RetargetRelation(RelationEdge<T>, newSource, newTarget)
DestroyAllRelationsBetween/Outgoing/Incoming<T>(...) // explicit bulk
```

endpoint 只能通过结构命令改变；retarget保留edge identity、payload和附加components。primary relation kind不能通过普通component Add/Remove建立、移除或换型；payload在endpoints不变时是edge entity上的普通component，可按edge chunk ranges并行读写，附加components也使用普通component API。

彻底删除旧 `AddRelation<T>(source,target)`、`ReplaceRelation<T>(source,target)`、`RemoveRelation<T>(source,target)`、`HasRelation<T>(source,target)`、`GetRelations<T>(source)`、`GetRelationSources<T>(target)`和对应compatibility wrappers。`(source,target)`只允许作为lookup或名字中明确含`All`的bulk selection；parallel relation的single-edge destroy/retarget永远要求edge identity。

以下全部是 derived immutable generations：

- outgoing adjacency；
- incoming adjacency；
- relation-presence/query index。

删除 `RelationStore<T>` forward/reverse side truth 和 `RelationTag<T>`。读取 adjacency 取得 generation-pinned borrow；old generation 最后一个 reader 释放前不回收。

relation order属于endpoint-local adjacency membership，不属于edge-global key或整个relation type。directed edge的source-outgoing与target-incoming memberships可各自有不同position；undirected edge在EndpointA与EndpointB的incident memberships也可各自有不同position。每个`(relation type, endpoint, adjacency role)` shard独立选择ordered/unordered，policy不传播到另一endpoint或其他shards。

ordered shard使用该membership专属的受保护order metadata，并支持append/prepend、stable edge anchor before/after、block splice、complete permutation与显式policy conversion。create/retarget进入ordered shard默认append，精确位置必须按outgoing/incoming或两个incident endpoint分别提供；retarget不携带旧endpoint下的位置。unordered shard只保存packed `(edge, other endpoint)` entries并允许swap-remove，不保存order key/index/monotonic sequence/renumber state，不承诺稳定枚举，也不在inner loop dispatch ordered logic。

unordered -> ordered接受完整edge permutation，未提供时只在该次conversion scratch按stable logical edge identity形成一次性初始顺序；ordered -> unordered丢弃该shard order metadata。转换只重建目标endpoint-role shard并发布新relation generation，旧pin继续观察旧policy/order。empty explicit ordered shard保留稀疏policy marker；普通unordered empty shard不为order分配状态。

可存储的 `RelationLookupHandle<T>`只声明未来topology access；owner admission后生成scope-bound `RelationLookupView<T>`。view提供`GetEndpoints(RelationEdge<T>)`以及generation-pinned `GetOutgoing(source)` / `GetIncoming(target)` read-only spans，child/edge topology没有writable span。payload随机访问另行声明`ComponentLookupView<T>`，使只读adjacency traversal不与payload writer产生假冲突。

directed lookup以`GetDirectedEndpoints`、`GetOutgoing`和`GetIncoming`暴露角色；undirected lookup以`GetUndirectedEndpoints`、`GetOtherEndpoint`和`GetIncidentEdges`暴露无向语义。SG根据relation direction拒绝在undirected T上请求outgoing/incoming或在directed T上请求incident-only view。undirected `ReplaceRelationEndpoint(edge, oldEndpoint, newEndpoint)`必须无歧义命中一个slot并保持另一个slot与payload slot association；whole-edge `RetargetRelation`始终可用。

普通`GetOutgoing/GetIncoming/GetIncidentEdges`只承诺membership；`GetOrderedOutgoing/GetOrderedIncoming/GetOrderedIncidentEdges`要求对应local shard当前具有ordered capability，否则稳定报错，绝不把packed physical order提升为业务顺序。positioned command使用role-qualified placement，不能用一个edge-global insert index。所有返回仍是generation-pinned scope-bound read-only spans，order metadata没有public writable ref。

direct relation query以edge entity为row identity，一条edge一行；generated callback可取得`RelationEdge<T>`、read-only endpoints和普通`RefRO<T>/RefRW<T>` payload。endpoint的`WithOutgoing<T>` / `WithIncoming<T>` filters是semi-join，每个endpoint entity至多一行，不因parallel edges重复endpoint row。需要per-edge展开的endpoint-edge join必须显式选择，并保持edge row identity。

undirected direct edge query同样一edge一row，但取得`UndirectedRelationEndpoints`；endpoint使用`WithIncident<T>` semi-join并且每个entity至多一row。edge-chunk payload RW因此仍有唯一row identity；incident expansion若要写edge或endpoint data必须通过alias matrix证明，不能从两个endpoints把同一edge写两遍。

relation topology change直接由protected ECS-native relation kind/endpoint/order components的`Added/Changed/Removed` query tracking表达；当前consumer使用保守的system `LastSystemVersion`窗口，未来若实现前述per-consumer checkpoint也只替换消费游标，不改变native facts。edge create是relation kind与endpoints Added，retarget是protected endpoints Changed，destroy是edge/relation kind Removed，local order/policy变化是对应protected sparse component Added/Changed/Removed；payload变化继续使用普通T component tracking。`Removed<T>`是consumer清理前保留的coalesced fact；同一change window内的remove -> re-add -> remove刷新其最终removed value与version，而不是重复添加cleanup component或制造第二套delta stream。删除`RelationChanges<T>`及任何relation-specific delta/change-stream API。事务prepare workspace可从stable command stream直接维护affected adjacency shards，但该workspace不进入World state、snapshot或public API，publish后丢弃。

source/target destroy、edge create/delete/retarget、local adjacency policy/order 和 presence/index publication 都在同一结构事务中处理。serialization 保存 canonical edge/endpoints/payload以及ordered shards的semantic edge sequence与empty policy，不保存gap/order allocator等历史相关物理key；unordered canonical export/state hash在显式scratch按stable edge identity排序，live unordered adjacency不持续付费。load 后重建 derived generations与物理order metadata。

relation adjacency access分两步：`LatestRelation<T>` capture在 semantic dependencies后线性化选择 current generation；`RelationGenerationPin<T>(g)` 只保护 immutable g 的 lifetime，不是会与 next publication冲突的普通 Read owner。publisher由结构 root swap串行化，old-generation reader不阻止 next publication。reader 要保证选择某次 transaction之后的 generation，仍需显式依赖 transaction handle。

## Hierarchy

hierarchy以statically typed domain区分独立 forests。每个domain公开同一组 ECS-native relationship shape：`Parent<TDomain>` 是一个child的canonical at-most-one-parent truth，`Children<TDomain>` 是由全部 Parent反推得到、可query但只读的 `RelationshipTarget` component/view。World的default domain只提供省略domain参数的 `Parent` / `Children` convenience facade，不另造一套storage或maintenance engine。同一Entity可在多个domains拥有互相独立的Parent关系；各domain分别做cycle validation并具有独立job resource identity。

hierarchy core没有membership概念：不存在 `HierarchyNode<D>`、Join、Leave或“全World属于某domain”的隐式状态。root只对具体 workload candidate set有意义：

```text
workload candidate + Without<Parent<D>> = 该 workload 的 root
workload candidate + With<Parent<D>>    = 该 workload 的 child
```

candidate由Transform、UI、scene或其他workload自己的普通components/query定义，也可包含当前Parent/Children关系中出现的entities。一个没有Parent、没有Children且不匹配任何workload candidate的isolated Entity，在hierarchy core中没有“member/non-member”差异。core因此不提供脱离candidate query的全局membership或无条件`GetRoots<D>()`；需要显式顶层集合的workload可以使用自己的root component或anchor Entity。

`Parent<D>` 是公开普通component并保留完整 native query/change语义。`Children<D>` 不是第二份authoring truth；用户只能通过 `RefRO`、scope-bound read-only span/view或可选snapshot读取，不能取得其 writable ref/span。empty Children的物理presence可由storage/policy优化决定，不能被用作membership tag。`ChildBuffer`、`HierarchyLink`及业务可写Depth全部删除。

### Immediate hook 与 deferred maintenance system

Parent lifecycle mutation支持两种由caller选择的 maintenance timing：

- **Immediate**：Parent add/replace/remove apply时运行 ECS internal relationship hook；hook调用共享parent-transition kernel，从old applied parent移除child、加入new parent的Children、维护local order并推进internal applied-parent。操作返回时Parent与Children一致。
- **Deferred**：只发布已验证的canonical Parent及其native change facts；Children保持last-applied inverse。generated job的 `ref Parent<D>` / writable Span天然属于此路径。之后统一hierarchy maintenance system按受影响parents分组，调用同一个transition kernel批量追平Children与applied-parent。

internal applied-parent是ECS维护状态，只记录Children当前反映的三态值（no parent或具体parent）；它不是public component model、第三份authoring truth或change stream。Immediate path同步推进它，因此maintenance后来观察到 `current Parent == applied parent` 时no-op；Deferred path保留它，因而即使Parent在maintenance前被多次改写或移除，system仍知道应从哪个old Children shard删除。Parent永远赢，system不从stale Children反推canonical truth。

relationship hook只维护 hierarchy invariant。它是ECS静态注册的internal hook，不是可替换的用户delegate，也不执行SomeUI `NodeHooks`、layout、mount、render或任意业务副作用。那些反应由普通systems/jobs消费native changes后产生 `LayoutDirty`、`DrawOrderDirty`、`MountPending` 等普通components。

同步World mutation和CommandBuffer都可在命令真正apply时选择Immediate或Deferred；CommandBuffer只决定transport/playback时间。raw ref/span不可能在每次字段assignment时拦截old/new target，所以固定Deferred。需要当前Children的consumer显式依赖对应maintenance handle；有意读取旧应用态的consumer只声明relaxed Children read而无需该semantic dependency。读取Children绝不隐式触发maintenance或隐藏write。

两条路径共享同一validation、transition、order和change-publication primitives，不复制两套ordered/unordered engines。Immediate mutation在返回前验证；Deferred ref/span owner在释放资源前对最终Parent preimage/overlay执行forest validation，失败或body fault时恢复其owned Parent rows并fault/cancel handle，因而不会把self-parent、dead-parent或cycle发布给后继owner。maintenance只修复合法Parent到Children的freshness，不负责猜测或容忍非法canonical topology。

### Native change tracking

hierarchy变化只通过普通ECS facts表达；当前由保守的system `LastSystemVersion`窗口消费，未来可在不改变facts的前提下接入per-consumer checkpoint：

```text
Added/Changed/Removed<Parent<D>>   canonical parent变化
Added/Changed/Removed<Children<D>> derived inverse应用变化
Added/Changed/Removed<local order/policy component> 局部顺序能力或顺序变化
```

`Removed<Parent<D>>`保留removed history所需的old value；maintenance同时使用internal applied-parent确定实际old inverse state。删除`HierarchyChanges`、`ParentDelta`、membership/order delta以及任何hierarchy-specific change-stream API。transition batch的affected-child/old-parent/new-parent scratch只属于当前owner，结束后丢弃，不能进入World/public API成为另一套delta。没有变化的maintenance通过native query window与applied-parent比较直接no-op，不要求每帧全表reconcile。

### Parent-local ordered/unordered Children

sibling order是一个domain内每个parent的direct Children collection的局部策略，不是整张hierarchy graph的模式，也不沿ancestor/descendant继承。一个child所在collection的策略与该child自己作为parent时采用的策略无关。

ordered parent shard保存该membership所需的order metadata并支持append/prepend、stable child anchor before/after、block splice、complete permutation和显式policy conversion。unordered parent shard只保存packed child entities，允许swap-remove，不保存per-child order key、order index、monotonic sequence或renumber state，不承诺稳定枚举，也不在iteration inner loop dispatch ordered logic。因此unordered parent不为ordered capability付storage、mutation或iteration成本。

进入ordered parent而没有显式placement的Immediate mutation默认按已线性化的apply顺序append。Deferred batch也使用append语义；多个同时进入者按`(recorded stable producer key, stable logical child identity)`排序，没有producer key的raw ref/span mutation使用stable logical child identity，绝不采用worker完成顺序、change scan顺序或packed storage顺序。要求精确位置的caller必须使用placement-capable typed mutation。进入unordered parent不读取或保留旧sibling position。

已有children的parent可以显式转换local policy。unordered -> ordered接受完整child permutation；未提供时只在本次conversion scratch按stable logical child identity形成一次性canonical初始顺序，绝不提升incidental packed order。ordered -> unordered丢弃order metadata。转换只触及目标parent shard；无关parent，尤其unordered shards，不付费。普通Children view对两种shard都提供scope-bound read-only traversal并只承诺membership；有序index/reorder API必须先验证该parent当前具有ordered capability。

### Mutation、destroy 与可选 traversal snapshot

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

## Transform / hierarchy propagation

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

## 其他 workload 规则

| Workload | owner/access | 并行规则 |
|---|---|---|
| unmanaged entity-wise POD | topology Read + matching chunk ranges | stable chunk batches；packet alias proof |
| chunk/SIMD | matching chunk ranges | spans 仅在 generated callback 内创建 |
| dynamic buffer | topology + header/payload/allocator accesses | 每 buffer 唯一 writer；resize epoch；thread-local/slab-safe allocator |
| sparse component | stable entity/shard ranges | add/remove membership走结构事务；value write按 shard/range |
| shared component | read按 shared/query ranges | value导致 relocation时走结构事务 |
| indexed component | component range + derived index write | per-batch journal，owner release 前 deterministic publish；不能 hidden lazy stale read |
| enablement | candidate chunk bit ranges | filter先拥有完整 candidate ranges；disjoint SetEnabled可并行 |
| random lookup RO | topology Read + whole component family Read | 仅当 owner内没有同-family packet Write |
| random lookup RW | whole component family Write | parallel 默认拒绝，除非专用已证明 partition |
| relation payload | edge chunk ranges | endpoints不变时普通并行值写 |
| structural recording | private command segment | producer间完全并行，不触碰 live topology |
| structural playback | topology Exclusive | stable merge、全批验证、原子 publication |
| reduction | per-partition accumulator | fast tree 可非确定；deterministic 模式固定 partition/merge tree |
| managed reference | whole family或 object-identity resource | 默认不并行 mutable object；外部 alias必须登记 |
| thread-affine | named executor + resource owner | route/validate main、render或device executor，不等同任意 caller |
| singleton/global | dedicated whole resource | mutable singleton不能被普通 packets并行写 |
| chunk/meta component | stable chunk-meta ranges | 每 chunk唯一 writer，跨 chunk并行 |
| event stream | private producer segments | stable logical key merge；与 command相同的 producer fault规则 |
| atomic/commutative | certified provider `ParallelWriter` | 只允许声明过的 operation；determinism另行选择 |
| GPU/IO | external owner | submit 前 admission，fence 后 release |
| cross-frame | scope/domain 持续存在 | 无 frame barrier；freshness 使用 explicit handle |
| multiple Worlds | sorted composite prepare | 按 stable World identity取得 permits/sequencers并原子 seal |

### Enablement

enable/disable不移动 component storage，但会改变 query membership，因此 `Enablement<T>` 是独立 bit-range resource。query先为所有 structural candidate chunks登记 enablement和data ranges，hazard完成后再读取 bits；`SetEnabled` 写对应 entity bit range并发布 change epoch。entity destroy仍由 topology guard保护。

### Indexed component

普通 raw writable Span不能同时维护 old/new keys。indexed type默认通过 `IndexedRefRW<T>` / indexed range wrapper记录 old/new；bulk adapter若必须给高吞吐写入，则在 body前 snapshot owned old keys，body后比较，或直接重建 affected index shards。body fault时 finalizer仍根据实际 dirty rows完成 index publication后再发布 WorkReleased；index必须反映 partial values。maintenance capacity在 body前准备，cleanup不得二次 allocation后留下 stale index。

### Dynamic buffer

buffer access拆为 chunk header、per-entity payload block和allocator arena。resize需要该 entity buffer Write + allocator access，并增加 per-buffer resize epoch；任何此前 element borrow下一次 dereference都拒绝。公开可 resize wrapper不同时导出长期 raw Span；raw Span callback拿不到 resize capability。disjoint buffers可用 per-worker slabs并行增长，同一 buffer永远只有一个 writer。

### Managed/shared aliases

parallel ref/span component默认只接受 unmanaged或metadata证明的 deep-value/non-alias storage。managed `Read` 只保护 component slot/reference，不证明引用对象内部 immutable；mutable object默认登记 whole family或 object-identity external resource，并在 named executor上 Execute。系统外未登记 alias不在 ECS 可证明范围内。shared/index hashing、equality、copy和potential disposal全部属于 structural root prepare/retire，不能进入 publish critical section。

## Heuristics

所有 heuristic 只选择 granularity，不改变可见语义：

- sync Execute 与 parallel dispatch：比较测得的 submit/steal 成本和 per-row/chunk cost estimate；
- entity batch 数：由 stable chunk count、worker count 和测得的 target grain 决定；
- hierarchy split：保留一条 local chain，sibling outbox 达到测得 batch grain后发布；
- hierarchy canonical forest validation（structural transaction或Deferred Parent owner）：small affected set用ancestor-walk，large/reparent-heavy overlay用dynamic-tree index，按测得operation count/depth cost切换；
- exact range 与 whole-family fallback：比较 canonical interval count/frontier cost，fallback 必须计数并可在 profiler看见；
- deterministic reduction：固定 partition count 和 merge tree，不自适应 worker timing；
- dirty full scan：只用于当前结构的损坏诊断/不变量验证，不是正常自适应路径。

阈值由 flat、wide、deep、many-roots、sparse-dirty、dense-dirty、zero-worker 和跨 World benchmark harness 固定。实现前必须报告公式、默认值和基准证据，禁止隐藏 magic number。

## Fault、cancel 与 cleanup

- `RequireSuccess` predecessor fault/cancel：body 不运行，pins释放，semantic successor fault/cancel。
- `AfterCompletion` predecessor fault/cancel：等待 scope terminal后仍运行，用于 retry/cleanup而非读取有效结果。
- hazard predecessor fault：successor 等其 WorkReleased 后继续。
- cancel before admission：不进入 frontier。
- cancel while waiting hazard：owner 从 frontier 释放并终结；later owners继续。
- cancel while running：只能协作式，body 返回前不能释放内存。
- child fault：parent WorkReleased 不受影响，parent ScopeCompleted fault。
- generic cleanup/release 设计为 nonthrowing；内部 scheduler failure记录为 owner fault但 terminalization必须继续。
- structural root swap开始后 cancellation延后；retire/observer fault隔离报告。若检测到已发布 root自身损坏，World进入 Poisoned/Fatal并拒绝 access，不能继续暴露可能不一致状态。

## Determinism

- 当前不把网络同步、lockstep、rollback或跨平台任意 simulation bitwise equality作为产品要求。
- ECS 必须保持 synchronization-ready：stable logical entity/producer keys、canonical serialization、replayable structural journal、deterministic structural ordering和显式 fixed-partition/fixed-merge execution points从现在起就是硬边界。
- 未来同步层只增加 ordered input stream、state hash/snapshot、rollback storage、deterministic math/FP policy和sync-specific systems；它不能要求替换 Resource Owner、Semantic Dependency、QueryHandle、Relation Edge、Parent/Children或Structural Transaction模型。
- coordinator 的 stable pipeline key/order稳定；并发外部 callers 的 admission order不承诺确定。
- independent entity outputs 和 hierarchy subtree outputs必须 order-independent。
- parallel commands按 coordinator/caller提供的 stable logical key和parent-derived producer path merge；deterministic mode拒绝无 key 的并发 external recorder。
- ordered hierarchy/relation views按其canonical semantic sequence枚举；unordered live views明确无顺序承诺。canonical serialization、state hash、stable error selection和显式sorted/debug view在scratch按stable logical id排序，不把稳定枚举成本转嫁给unordered hot path。
- floating reduction默认不宣称 deterministic；replay/network模式使用固定 partition tree。
- 多 fault 聚合按 logical job id排序，不按到达时间。

为避免阻塞未来同步，canonical state和journal禁止包含 worker id、线程时序、内存地址、dictionary enumeration order或未捕获的 ambient clock/random input。scheduler可自由并行和work-steal，但其 timing不可进入逻辑 identity或serialized state。

## 必须通过的验证

### Scheduler

- pending explicit dependency不预占资源，later ready conflict可前进；
- parent/pending dependent/dynamic child反例不死锁；
- semantic self/ancestor/transitive/combined-handle cycles被拒绝；
- WorkReleased 与 ScopeCompleted 分离；
- Execute 的 before/after admission race、zero-worker pumping、body fault cleanup；
- disjoint/overlap/whole ranges、whole Read + subrange Write mode map、多资源反向声明、数千 intervals 不退化为 O(N²)；
- topology prepare在 resolve/cancel/Closing/seal fault时完整 rollback，且未 seal owner不可运行；
- A/B 与 B/A 跨 World submissions按 sorted composite prepare前进；
- old runtime handles不提前 completed，cross-generation dependency拒绝。

### Borrow/query

- sync foreach整个枚举持有 owner，break/exception可靠 Dispose；
- async view只在 body/batches内创建；
- enumerator copy、manual early Dispose、manual non-dispose、borrow copy和owner外 ref/span extraction全部拒绝；
- `ref T + indirect RO lookup<T>`、buffer/index/relation等同-family cross-packet self-alias在 SG负例中拒绝；
- stale World/storage/snapshot generation拒绝；
- nested subset borrow合法，upgrade/additional access拒绝；
- schedule-after-wait race不能与同步 ref/span 重叠；
- Changed writer-predecessor late-start、out-of-order completed epochs、enablement/membership变化不漏报；
- future checkpoint gate：copied `QueryCheckpointHandle`共享同一terminal lane，fault后at-least-once retry不形成永久fault chain；
- future checkpoint gate：same-scope descendant复用checkpoint、pending K consumer依赖parent再由parent child复用K时确定性拒绝，不形成scope环；
- dynamic-buffer resize使旧 element borrow失效，disjoint buffer growth仍可并行；
- indexed bulk write success/fault都发布与实际 values一致的 affected shards；
- managed object alias、named executor mismatch、mutable singleton和event producer fault按声明规则拒绝/终结。

### Relation/structure

- first/last edge、exclusive uniqueness、source/target destroy；
- outgoing/incoming generation borrow与并发 publication；
- `Parallel/UniquePair/UniqueSource/UniqueTarget/OneToOne` create与retarget collision matrix；同pair parallel edges保持不同identity、payload和lifetime；
- directed source/target role与undirected incident graph；undirected reversed-pair uniqueness、parallel edges、degree-one、self-edge和single-edge canonical truth；
- undirected endpoint slot与payload A/B association在create/retarget/serialization后保持；禁止用两条directed edges模拟或暴露undirected source/target API；
- directed outgoing/incoming与undirected两端incident order完全独立；同一edge两端位置可不同；edge-global order API不存在；
- ordered adjacency create/retarget placements、anchor/block/permutation与policy conversion；unordered instrumentation为零order-key bytes、零renumber、零ordered inner-loop dispatch且枚举无稳定承诺；
- single-edge destroy/retarget只接受`RelationEdge<T>`；endpoint-pair旧API在public reference与binary API shape中均不存在；explicit `DestroyAll...` bulk无歧义；
- direct edge query一edge一row，outgoing/incoming endpoint semi-join不重复endpoint；adjacency borrow与payload component access使用独立resource declarations；
- relation topology通过protected endpoint/order components、hierarchy topology通过public Parent与read-only derived Children的Added/Changed/Removed + 当前保守system query window消费；未来per-consumer checkpoint只可收窄重复，public API与metadata中仍不存在RelationChanges、HierarchyChanges、ParentDelta或其他topology delta stream；
- stable-key parallel command merge重复运行 byte-for-byte一致，无 key recorder在 deterministic mode拒绝；
- stable sequential candidate replay覆盖 SetParent/Destroy/DestroySubtree/Reorder/deferred create/relation retarget全部组合；
- prepare/hash/equality/allocation fault后 old root不变；一次structural transaction的root swap原子发布其prepared canonical/derived/journal/epoch，同时允许另行声明的Deferred Parent在maintenance前领先Children；
- producer/descendant fault使 buffer Failed并丢弃 partial segments；
- deferred identity mapping、single playback、Dispose不改 live World。

### Hierarchy/transform

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

### Lifetime/external

- Closing拒绝 roots但允许 existing descendants；
- system remove/World dispose等待 pending-yet-unadmitted jobs和 external owners；
- self-dispose、child-close-parent domain和持有 B permit时 close B立即拒绝而不自锁；
- external work绝不在 prior CPU hazard结束前 submit；
- submit fault、signal race、device loss、永不 signal诊断不提前释放；
- Fast模式也不能释放 active/pinned resource。

## Breaking cutover 与完整交付

在当时的 cutover 决策点，已知用户均在仓库内，且没有已发布 workload 依赖这些旧接口，
因此采用 deliberate breaking cutover。此句只记录当时的兼容性判断；当前仓库已经有 Core、
Render、Systems、serialization、benchmark、fuzz 与 NativeAOT smoke 等实际 consumer：

- 不保留本次 cutover 已点名旧 ECS API 的 source、binary 或 runtime symbols；
- 不提供 obsolete wrapper、转接层、feature flag或新旧双 ownership model；
- 若仓库外另行提供机械源码改写工具，它也不能通过重新声明旧 symbol让旧代码继续编译；
- 仓库内 product code、tests、samples、serialization integration、SourceGen、wiki和harness在同一 end-to-end delivery中全部迁移；
- 只有新 owner/borrow/structural-root模型成为唯一产品路径、旧 API 搜索结果归零且完整 harness通过，才算完成；
- 不能只交付底层 JobSystem后把 ECS query、Relation、Hierarchy、CommandBuffer或System迁移留作后续 handoff。

同样不保留 pre-cutover ECS serialized World、scene、delta、snapshot或测试 fixture兼容：

- 唯一普通序列化 wire 是 version 4；任何其他 envelope version 都 fail closed，不做 best-effort转换；
- 不为 `RelationStore`、`RelationTag`、`ChildBuffer`、`HierarchyLink`或旧 entity-location布局编写 importer；
- 每个 type key只有一个非零 64-bit schema fingerprint；unknown type/topology、zero/mismatch fingerprint、旧32-bit字段、旧Guid/string编码、length-prefix frame和unversioned delta section全部拒绝；
- item与topology codec直接向最终sink编码一次并追加length footer；不存在dry pass、第二次encode、encoded frame backing、topology snapshot DTO或mmap副本；
- `WorldCheckpointCodec`只接受`SEWCP003`，其128-byte header包裹一次相同的canonical `RawCheckpoint` World payload；不存在section directory、checkpoint专用component/topology codec、NativeRaw dump或non-seekable整包暂存；
- `ReadWorld`直接构造一个新的最终World；slot、component/buffer、hierarchy shard和relation generation边读边进入最终backing，失败即Dispose。不存在`LoadInto(existing World)`、candidate apply、capture DTO或失败原子Apply兼容入口；
- runtime不提供schema转换注册表、unknown skip或旧primitive decoder。schema/wire变更是breaking data change；产品若要转换已发布文件，只能另行提供runtime之外的显式离线工具。

在该决策发生时，仓库没有需要保留的已发布 ECS consumer 或持久化 workload，因此兼容层、
旧数据转换器和双格式 reader 不属于那次 cutover 的产品交付。当前 durable-save 和其他
serialization workload只接受各自的current envelope、current schema和认证模式；不会恢复
任何pre-cutover或未来旧版本格式兼容。

## 当前实现的替换边界

- `RunQuery` / `RunReadWrite`、`QueryBuilder` / `QueryView`、`CreateQuery` / `GetQueryState` 已从产品实现删除。公开执行入口只有runtime-owned `ExecuteQuery` / `ExecuteReadWrite`；`QueryChunkView`、`QueryRow`、cursor与span只在该callback owner内创建。公开`ExecuteQuery`不接受caller-supplied current version，writable/read-only路径在exact admission后分别自动取得新write version或保持clock不变；显式current overload只供runtime内部使用。
- `World.Get<T>() -> ref T` 与 `ReadRef<T>` 已删除。copy read、value setter和structural setter形成短同步 owner；buffer、sparse、query和bundle等返回ref/span/view的入口都已改为runtime callback，而不是降为internal compatibility API。
- 组件约束只保留根命名空间的 canonical `IComponent` / `IEnableableComponent` /
  `ICleanupComponent`；`SomeEngine.ECS.Components.*` compatibility aliases 已删除。Core 只使用
  `ImmediateSystemContext` / `ImmediateSystemDriver`，重复且纯转发的
  `EngineSystemContext` / `EngineDriver` 也已删除。consumer 直接迁移，不保留 shim。
- Job resource declaration现在也是运行时capability：验证resource identity/runtime generation、Read/Write覆盖、whole/range覆盖、连续range union和single-work-item约束，Fast mode不关闭这些正确性检查。显式dependency等待期间只保留identity/lifetime reservation而不进入conflict frontier；ready时原子激活。大range set不会扫描同一owner刚登记的其他slices，释放时按resource一次移除该owner全部slices；current-scope capability以mode-aware merged interval index做精确union/gap查询，scratch与registration data均池化复用。
- ECS core正式引用Job的轻量execution-context contract，但不访问或初始化`JobSystem`；logical World storage到resource identity的映射仍由Systems安装。缺少mapper的raw Job访问World会fail closed，不能伪装成同步caller。first typed binding与unbound World entry在同一gate线性化：unbound callback先进入时binding立即fail/retry，不等待成callback -> Complete -> binding死锁。绑定后caller-thread同步调用取得真实Resource Owner：先等待既有冲突、在完整World调用期间留在frontier、释放后才允许后提交的冲突owner运行。Core/Dots所有公开Execute contract还会在任何调度副作用前拒绝async state machine；判定按closed generic job/contract缓存，work-item热路径零反射。
- Parent、directed endpoints与undirected endpoints均有对称serial read/write chunk job adapter。adapter在schedule boundary先验证non-optional whole-chunk query shape，再声明domain/payload local resource与World topology resource；写路径在整个owner结束时验证final forest/cardinality image并完整恢复preimage。普通parallel query仍不能写canonical relationship；Parent的multi-work-item入口只有带`StructureEpoch + TopologyRevision`、checked prefix/full-tail proof、精确staging coverage和单一publication finalizer的`TopologyPacketFinalizer<D>`。capture以count/fill两遍直接建立一份完整`CapturedParents` preimage；每个packet只借短生命周期、finally清零归还的pool scratch，长期stage只保存严格packet-local的稀疏changed-row edits，不保留第二份完整Parent backing。公开handle是整个capture-root及动态后代结束后的唯一terminal observer；dependency、packet或final-image fault一旦观察就永久锁存failure，之后不能取得partition proof。packet staging只有`LastSystemVersion`而没有`CurrentSystemVersion`；唯一commit version在admitted finalizer的candidate内取得，empty/no-op不消耗tick。
- relation canonical truth已是独立edge Entity + protected endpoint components；`RelationStore`、`RelationTag`、endpoint-pair-as-edge API和relation-specific delta均已删除。adjacency使用immutable generation，旧snapshot可继续读取。serialization import的ordered sequence用方法/sequence局部dictionary做O(1) duplicate membership检查，完成后立即清空；该dictionary不是长期relation结构，最终truth仍只有edge components与generation-owned entries。
- hierarchy canonical truth只有`Parent<D>`；`Children<D>`是只读last-applied inverse。旧`ChildBuffer`、`HierarchyLink`、`HierarchyNode<D>`、Join/Leave、`HierarchyChanges`和重复ordered/unordered engines均已删除；immediate hook与deferred maintenance复用同一transition kernel。
- ordered/unordered policy只属于parent或endpoint-role shard。可执行diagnostics覆盖flat、wide、deep、sparse-dirty和mixed workload，证明纯unordered路径没有order-key payload、ordered index work、ordered dispatch或pending placement metadata；mixed maintenance只copy实际触碰的parent-local shard。
- CommandBuffer已使用command-local `DeferredEntity` / deferred edge identity，不在record时占用真实World slot。完整FIFO在精确detached candidate root中只回放一次；success才通过单一`WorldStructurePublication(root, epoch)`解析handles并发布，failure、hook fault、allocation fault或invariant fault都会丢弃candidate与hook command overlay。普通共享CommandBuffer不是Job API：Job-bound World上的record/playback/count/clear/dispose全部在side effect前拒绝，topology Write也不能绕过；同步`World.Flush`在移除非空wave之前取得topology Write，拒绝的Job不会消费wave或污染single-playback状态，empty wave仍不取得资源。
- mutable candidate root的entity-location pages与table chunk backing已经是persistent COW；record page只存identity/scalar，不引用archetype/chunk/root shell，live location通过当前root解析。未触碰page/chunk共享，第一次真实写在bounded backing内detach一次；empty prepare/load/replace、equal enable write和无selected table column copy不会误detach。old-root read不会触发detach。DynamicBuffer header携带per-row overflow owner identity：fork、move、swap-remove与promotion可继续共享只读backing，只有目标row第一次真实内容写才分离，普通component write不遍历或复制其他overflow；detach保留capacity但只复制`[0, Count)`，inactive capacity为`default`，managed overflow的inline storage保持清空，同generation重复写不再detach。每个shared bucket只有一个canonical immutable shared-component tuple/backing，同bucket chunks与detached forks复用它，raw mutable backing不暴露，chunk detach也不复制它。exact clone只做一次record resolver scan。其余owner保持exact detached clone。atomicity与COW tests覆盖table/buffer/sparse/shared/index/hierarchy/relation/journal/tick/allocator/free-list/immutable spans、hook fault、allocation fault、并发old-root reader和并发first writers；剩余COW扩展只是profiling驱动的prepare优化。
- `BundleWriteView`、buffer view、dynamic buffer、sparse callback与typed component job access已经关闭public ref/span逃逸。bundle batch逐行lazy materialize并复用runtime/chunk；captured-World write与pending indexed-component backfill有机械guard，tag descriptors写native `TagAdded` journal。
- 导出的 `BufferView<T>`、`BundleWriteView`、`QueryChunkView` 全部是 `ref struct`；可存储的
  children generation 唯一命名为 `HierarchyChildrenSnapshot<D>`。API shape测试会拒绝新的
  非-byref-like `*View`。公开无参 `ToArray` 只允许
  `HierarchyChildrenSnapshot<D>` 与 `RelationEdgeQuery<T>` 两个显式snapshot物化入口。
- 同步user hook保持立即执行；`DeferredWorld.Commands()`返回只在本次thread/epoch有效的record-only writer并稳定进入next wave。captured World的table/shared/topology write拒绝，component-local fast path不能绕过；runtime-owned direct buffer/sparse borrow可复用已有topology writer，Job内还要精确capability。public serialization-journal suppression只有同步callback overload，没有可逃逸的`IDisposable` token；它在callback结束/抛错时先恢复journal再释放topology control owner，Job callback一律拒绝，无实际topology mutation时不发布假revision。candidate ECS state与command overlay可回滚，任意World外部副作用、`ref TState`和fault transaction泄漏的raw Entity不在回滚保证内。
- generated `IJobEntity`、topology capture和hierarchy propagation的resource-bearing capture owner都直接挂在semantic dependency上，packet work作为attached children延长同一lease。`IJobEntity`的empty/fully-filtered/matching-read-only执行不取version；parallel writable packets只在首个matching row惰性共享一次。hierarchy propagation即使零root也验证access set，user callback只能通过typed context访问ECS；静态或预构造的World/query/hook/CommandBuffer/tick逃逸在side effect之前拒绝，且只有首个真实typed-context Write在全部guard后取得共享version。topology pipeline只保留packet后的resource-free finalizer launcher；empty/no-op staging不会登记World writer、取得commit version或发布revision。
- system-level access manifests、stages、`GlobalDependency`和frame-global Complete已删除；system callback只保持稳定registration顺序并显式返回/传递JobHandle。
- serialization使用post-cutover topology format，保存canonical Parent/order与relation edge/endpoints/order，load后重建derived inverse；reader要求topology section count、registry ordinal、stable key/name/schema与Parent/edge/ordered-sequence record顺序全部canonical exact match。Hierarchy import先把Parent直接写入最终backing，再以最终applied-parent map加import-local dictionary做一次O(P) cycle seal并清空临时metadata，才允许发布inverse shard；不存在长期第二graph、旧side-store/hierarchy格式reader。checkpoint registry identity也包含length-delimited stable name，空World中未出现的registered type改名同样拒绝。writer admission显式持有入口时的`WorldStructureRoot`，验证后发布语义相同的COW successor并释放topology admission；codec、manifest、entity和topology编码全部直接读retained root。普通World query/mutation/write只解析candidate或published root，不查thread-static serialization context，也没有serialization validation分支。codec或output callback若捕获World，只能看到并修改successor；已开始的payload仍来自retained root。同World递归capture因旧root publication失效而稳定拒绝，异Worldcapture彼此独立。
- 运行时代码当前59个 `.ToArray()` 调用逐文件登记在materialization-boundary gate中；每项带
  精确调用数和上述边界理由。任何新增、移动或数量变化都必须先审查，不能在query、
  mutation或write inner loop中悄悄引入collection copy。descriptor shape gate同时扫描
  Systems与serialization assemblies，拒绝任何单字段具体`*Desc/*Descriptor`。另外，
  ECS、Systems与ECS.Serialization三个程序集当前20 + 8 + 4个单字段具体类型全部进入带
  独立职责说明的inventory；新增候选必须证明identity、invariant、lifecycle、storage或
  algorithm responsibility，纯转发类型不能通过更新清单获得正当性。

上述correctness边界已经实现：entity page/table chunk persistent COW、generated general `IJobEntity`/query access set、full-tail stable packet/range proof、multi-work-item topology finalizer，以及typed-maintenance parallel hierarchy/Transform propagation都具有可执行证明。仍可按profiling扩展COW到sparse set、index、shared store、relation/hierarchy mutable state和journal pages；这是prepare-cost优化边界，不是缺失的事务、alias或publication correctness。

## 参考实现取舍

- Unity Entities：采用 `IJobEntity`、`in/ref` access inference、`RefRO/RefRW`、typed lookup 和 deterministic parallel command思想；不采用任意关闭 parallel restriction 或 frame-global dependency 心智。
- Bevy ECS：采用 query borrow、Parent source-of-truth、derived Children 和 unique-subtree propagation proof。
- Unreal Mass：采用 query/chunk/range contention，而不是 system-wide stage。
- Flecs：采用 per-thread staged commands 和 stable partitions。
- Taskflow/Rayon/oneTBB：采用 structured descendants、cooperative wait 和 external activity lifetime。

外部证据：

- [Unity IJobEntity](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/iterating-data-ijobentity.html)
- [Unity SystemAPI.Query](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-systemapi-query.html)
- [Unity ComponentLookup](https://docs.unity3d.com/Packages/com.unity.entities@1.4/api/Unity.Entities.ComponentLookup-1.html)
- [Unity deterministic ECB playback](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-entity-command-buffer-playback.html)
- [Bevy Query](https://docs.rs/bevy_ecs/latest/bevy_ecs/system/struct.Query.html)
- [Bevy ChildOf source of truth](https://docs.rs/bevy/latest/bevy/ecs/prelude/struct.ChildOf.html)
- [Bevy transform propagation](https://github.com/bevyengine/bevy/blob/main/crates/bevy_transform/src/systems.rs)
- [Roslyn source generators are additive, not call-site rewriters](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)
- [Rayon structured scope](https://docs.rs/rayon/latest/rayon/fn.scope.html)
- [oneTBB external async node](https://oneapi-spec.uxlfoundation.org/specifications/oneapi/latest/elements/onetbb/source/flow_graph/async_node_cls)
