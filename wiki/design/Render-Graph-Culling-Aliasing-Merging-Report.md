# Render Graph culling、alias placement、render-pass merging 与 RHI 边界报告

> 日期：2026-07-11
>
> 状态：调研结论已经落到产品代码，并完成 backend-neutral 与 D3D12 WARP correctness 验证；仍不声称已有真实游戏负载收益。
>
> 范围：pass culling、transient alias placement、render-pass merging、CPU-visible placed resources、MSAA texture 到 linear buffer 的路径、Render Graph texture view。

## 结论

| 主题 | 性质 | 结论 | 当前是否应默认启用 |
|---|---|---|---|
| exact-range pass culling | 精确 correctness lowering，不是 heuristic | 已实现；conservative 与 optimized plan 得到同一 live set | 已启用 |
| compiled execution batch / record unit | 后续优化的结构前提 | 已建立；允许同队列多个 record unit 合入一个 execution batch，不改变 logical pass 身份和公开即时语义 | 不是开关 |
| alias eligibility | 精确安全证明 | 只使用已经存在的 happens-before，不为省内存增加 GPU 串行；仅在 alias policy 开启时构造 dense reachability 与候选 | placement 优化暂不默认启用 |
| alias placement | 确定性 bin-packing heuristic | 使用 lifetime-aware best-fit decreasing；必须有 no-alias fallback 和真实负载报告 | 暂不默认启用 |
| raster merge eligibility | 精确兼容性证明 | 只看 culling 后相邻 live raster pass，不重排；merge policy 关闭时跳过候选分析 | 暂不默认启用 |
| raster merge selection | 会影响 native pass 数、tile traffic 和并行录制 | 使用 stable adjacent greedy；真实负载前不引入收益打分、重排或 vendor 规则 | 暂不默认启用 |
| explicit MSAA resolve | 明确的语义能力 | RHI command/state 与 RG access 已实现；MSAA→buffer 继续拒绝并要求显式 resolve | 已支持 |
| RG texture view shape/format | 资源与 shader ABI contract | explicit view dimension、cube compatibility、allowed view-format set 和 shader shape 校验已实现 | 已支持 |
| CPU-visible placed buffer | RHI 完整性能力，不是 RG transient alias | Upload/Readback placed buffer 已实现；CPU-visible texture 继续禁止 | RHI 已支持，RG transient 不开放 |

最根本的结构结论已经落实：“一 logical pass = 一 command context = 一 command list = 一 submit”已被更高层的 compiled execution batch / record-unit IR 取代。culling 产生 authoritative live set；queue/hazard/barrier 在 live graph 上生成；alias acquire、imported boundary barrier 和 raster scope 都成为没有公共图身份的 compiled execution semantics。

## 已交付实现

本报告提出的核心结构已经落地：

- `GraphLiveness` 用 buffer endpoint compression 与 texture mip/layer/aspect cell 生成 exact producer prerequisites，从 imported-write roots 反向求 live closure；dead pass、resource 与 view 不再进入 realization、record 或 submission。
- `CompiledGraph` 是透明缓存保存的 immutable lowering payload；`CompilationCache` 才是 exact-key、single-flight、LRU、publication、lease 与 retirement 的缓存容器。缓存默认开启，公开 API 仍然每 invocation 即时记录真实图。
- `CompiledGraph` 是纯托管计划，只由当前 invocation 的 CPU lease pin；native resource/view/descriptor/command object 由 RHI 按精确 queue completion 独立 retirement，GPU completion 不会延长计划缓存生命周期。
- conservative 与 optimized lowering共享 authoritative live set。optimized plan可按 policy 使用 alias placement 与 raster merging；二者默认关闭，policy bits进入 cache environment，不能错误复用另一策略的计划。
- alias/merge policy 都关闭时，cache miss只产生当前 invocation需要的 conservative plan，不启动仅改变 `Optimized` 标志的重复后台 flight；需要优化时仍是 exact-key single-flight，由 coordinator 在安全边界发布且只影响后续 invocation。
- `CompiledExecutionBatch` 与 `CompiledRecordUnit` 已替代执行器的一 pass 一 submit假设；一个 batch 可以包含多个 record unit。logical pass身份和 callback保持；alias acquire与 imported whole-resource return transition成为无用户 callback的 internal unit；兼容的相邻 raster pass可成为一个 `RasterScope` unit。
- `CompiledGraphContract` 在计划执行或发布前校验 live mask、dependency/execution topology、queue、placement、raster scope、alias acquire与完整 barrier state machine。
- alias allocator只复用 execution DAG已经证明 happens-before可比较的 device-local transient lifetime，不添加新的 GPU串行边；placement使用报告中公开的 deterministic lifetime-aware best-fit规则。
- raster grouping使用 stable adjacent greedy；attachment、load、recording lane、barrier、alias acquire、cross-queue schedule 与首次跨队列 imported readiness都会形成可诊断 break reason。
- RHI/Null/D3D12已支持 Upload/Readback placed buffer、显式 color MSAA average resolve、明确 texture view dimension、cube-compatible creation与 immutable allowed view-format set。直接 MSAA texture↔linear buffer copy仍在公共验证层拒绝。
- D3D12 persistent CPU descriptors使用 device-owned typed page pool并随 native view/sampler fence retirement归还；buffer/texture allocation requirements按去除 debug name后的精确 descriptor缓存；默认 `TextureDesc` view-format路径不再构造排序集合。
- shader artifact并行保存 `DeclaredEffect`、`ReflectedAccess`、`DeclaredOperations` 与 `ReflectedOperations`，RG同时分析四者，且 texture dimension/sample/storage-format shape参与 shader binding校验与 canonical cache data。当前仅精确 `ReadWrite` storage上的 `Atomic`进入安全图路径；Append/Consume/RasterOrdered/Feedback完整保留信息但在RG admission fail-close。

当前默认策略是：exact culling与透明 compilation cache开启；alias placement与 raster merging关闭，等待代表性游戏负载决定是否默认开启。关闭后仍运行确定性的 correctness lowering，不构造这两类候选分析，也不启动无差异 optimized flight，不改变即时语义。

## 实现前基线审计

本节保留改造开始时的基线，用于解释为什么需要上述结构；其中“未实现”描述的是改造前状态，不是本文当前交付状态。

### 编译器

当前 [`Compiler.Compile`](../../src/SomeEngine.RenderGraph/Compilation/Compiler.cs) 的顺序是：验证全部 pass，给全部 pass 选 queue，给全部 pass 生成 rendering/dependency/barrier，然后给全部 transient resource 顺排 placement。

具体状态如下：

| 能力 | 当前实现 | 结论 |
|---|---|---|
| culling | 没有 live mask、root 或 value producer graph | 未实现 |
| dependency | 对 earlier pass/access 两两比较，同时混合 hazard 与跨 queue state-domain ordering | 不能拿来做 culling producer graph |
| barrier | 扫描全部 pass；支持 transition 与 UAV ordering | 没有 live filtering、alias acquire 或真正 release/acquire |
| placement | 按 `MemoryType + ResourceHeapClass + CompatibilityClass` 分组后 bump allocation | 是合法 no-alias baseline，不是 alias allocator |
| rendering | 每 pass 只有一个 `(Width, Height)` | 没有 attachment store、resolve、scope/group 或 break reason |

现有 `BuildDependencies` 不能直接反向遍历做 culling，原因包括：

- WAW hazard 会保留已被 full/discard overwrite 的旧 writer；
- buffer 的跨 queue state domain 当前会保守串行不重叠 range；
- barrier/ownership edge 表达执行安全，不等于 downstream value 依赖；
- culling 必须回答“哪个 exact producer 提供了被观察的值”，hazard graph 回答的是“哪些访问不能无序重叠”。

这两类 edge 必须分开保存和诊断。

### 执行器

当前 [`GraphInvocation`](../../src/SomeEngine.RenderGraph/Execution/GraphInvocation.cs) 会：

- 创建全部 compiled heaps；
- 实现全部 imported/transient resources 和全部 views；
- 给每个 frozen pass 租一个 command context；
- 每个 pass 单独 `BeginRendering` / callback / `EndRendering`；
- 每个 pass 单独 finish 和 submit。

因此：

- dead pass 即使未来被标记，也仍会分配 resource/view/context，除非执行器全面改用 live masks；
- 多个 logical raster pass 无法共享 native raster scope；
- alias ownership 切换没有独立执行节点；
- D3D12 suspend/resume 所需的“一次 ExecuteCommandLists 提交连续 command lists”无法表达。

### RHI 与 D3D12

已经具备的基础：

- committed / placed buffer 和 texture；
- native resource size、alignment、heap class 和 opaque compatibility class；
- physical allocation identity 与 byte range；
- abstract `BarrierKind.Aliasing`；
- D3D12 `D3D12_RESOURCE_ALIASING_BARRIER` lowering；
- buffer↔single-sample texture copy footprint；
- depth/stencil plane-aware view、barrier 和 copy；
- Upload/Readback committed buffer 与 CPU read/write。

仍然缺失：

- resolve command 与 ResolveSource/ResolveDestination state；
- native render-pass capability snapshot 和 BeginRenderPass lowering；
- CPU-visible placed buffer requirements/query；
- explicit texture view dimension；
- resource creation-time cube/mutable-format contract；
- shader texture shape/sample-type validation；
- Null backend 的 active alias owner、poison 和 native raster-scope oracle。

当前 D3D12 `BeginRendering` 使用 `OMSetRenderTargets`、Clear/Discard；没有调用 `ID3D12GraphicsCommandList4::BeginRenderPass`。当前 RG 和 D3D12 view 都拒绝 `view.Format != resource.Format`，且 backend 根据 resource 的 depth/array/sample fields猜 SRV dimension。

## 一手资料对比

### Pass culling

UE RDG 公开了 unused pass/resource culling、texture subresource producer、`NeverCull`、readback root、culling debug switch 和 RDG Insights；UE 的普通 execute lambda也被要求没有任意 host side effect。[UE Render Dependency Graph](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine)、[ERDGPassFlags](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/ERDGPassFlags?lang=en-US)

Unity 默认允许 pass culling，写 imported resource 或显式关闭 culling 会建立 side effect/root。公开 compiler 使用 versioned resource producer 做 upstream reachability，并提供 Render Graph Viewer、culling disable 和 pass-break diagnostics。[Unity RenderGraph source](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Compiler/NativePassCompiler.cs)、[Unity Render Graph Viewer](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-viewer-reference.html)

Google Filament 使用 pass/resource dependency graph和 target/side-effect root，反复删除没有下游引用的 sink，随后只为 active pass 计算 resource lifetime。[Filament FrameGraph](https://google.github.io/filament/notes/framegraph.html)、[Filament source](https://github.com/google/filament/blob/main/filament/src/fg/FrameGraph.cpp)

SomeEngine 不照搬三者的通用逃生口。已接受的语义仍然是：present、extraction、readback、imported write 和具体 external interop contract 是 Observable Graph Output；任意 execute callback、coordinator lane、pass name 或 profiler marker都不是 root。这样才能让 culling、缓存命中和并行录制拥有稳定语义。

### Transient aliasing

UE 官方确认 transient allocator在 graph compilation 中规划分配，disjoint lifetime resource 可以重叠，并提供 transient allocator关闭、extended lifetime和 RDG Insights 内存布局诊断；但公开资料不足以证明 UE 使用某一种具体 bin-packing 算法，因此本报告不把自选 heuristic 冒充为 UE 算法。[UE Render Dependency Graph](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine)

D3D12 placed resources可以重叠；simple model 下新 owner 需要 alias barrier激活。RT/DS resource在激活后还必须用 Clear、Discard 或完整 Copy 初始化。微软建议优先使用 simple model以获得更完整的工具支持。[CreatePlacedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createplacedresource)、[Memory Aliasing and Data Inheritance](https://learn.microsoft.com/en-us/windows/win32/direct3d12/memory-aliasing-and-data-inheritance)

Vulkan 没有同名 alias-barrier command，但同一 memory range 的 alias use需要正确 memory dependency；image takeover还涉及 undefined contents/layout、memory type、alignment、buffer-image granularity和 dedicated-allocation约束。[Vulkan memory aliasing](https://docs.vulkan.org/spec/latest/chapters/resources.html#resources-memory-aliasing)、[VK_IMAGE_CREATE_ALIAS_BIT](https://docs.vulkan.org/refpages/latest/refpages/source/VkImageCreateFlagBits.html)

因此 abstract alias acquire不能在 future Vulkan backend 中 lower 成 no-op，也不能只携带 D3D12 resource pointers。compiled intent必须能让 backend得到 prior/new resource、overlap range、source/destination access domain、queue ownership和 new image初始 layout语义。

### Render-pass merging

UE 官方开关将其描述为合并 identical contiguous render passes，Immediate Mode会关闭 merging，RDG Insights展示 merge 结果。[UE command-line reference](https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-command-line-arguments-reference)

Unity native-pass compiler在 culling 后做确定性前向 merge，并在 Viewer 中展示 merge bar、最终 load/store和 pass-break reason；官方优化指南要求只有 framebuffer-local current-pixel read才使用 input attachment，而普通 texture sample不会自动视为 local read。[Unity Optimize a render graph](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-optimize.html)

D3D12 native render pass固定 output bindings并把 beginning/ending access、discard/preserve/resolve作为 scope metadata。Ending resolve可在 tile contents仍驻留时完成；suspend/resume可跨 command list，但相邻 lists必须在一次 `ExecuteCommandLists` 中执行。[BeginRenderPass](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist4-beginrenderpass)、[D3D12 ending access](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_render_pass_ending_access_type)、[D3D12 render passes](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/d3d12-render-passes)

Vulkan dynamic rendering attachment metadata同样包含 load/store/resolve；modern local-read和 suspend/resume可表达部分 native fusion，classic subpass仍可作为旧设备 lowering。compiled IR因此应描述 raster scope/segment/local dependency，而不是暴露 Vulkan subpass作为 RG 公共概念。[VkRenderingAttachmentInfo](https://registry.khronos.org/vulkan/specs/latest/man/html/VkRenderingAttachmentInfo.html)、[dynamic rendering local read](https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_dynamic_rendering_local_read.html)

## 统一的 compiler pipeline

目标依赖顺序是：

```text
Frozen full graph
    → validate every declaration and content contract
    → normalize exact buffer intervals / texture cells
    → build value-producer prerequisites
    → discover Observable Graph Output roots
    → exact backward liveness closure
    → authoritative live pass/resource/view sets
    → queue selection and execution DAG
    → live-only hazards and state/ownership barriers
    → happens-before reachability and lifetime frontiers
    → safe alias candidates and optional placement
    → attachment store/resolve decisions
    → adjacent raster-scope grouping
    → compiled execution batches / record units
    → invocation binding, recording and submission
```

必须维持三种互不混淆的关系：

| 关系 | 用途 | 是否影响 culling |
|---|---|---|
| value-producer prerequisite | 哪个旧内容生产者使当前结果可观察 | 是 |
| execution hazard / ownership edge | 哪些 live accesses必须有序、在哪个 queue同步 | 否，只作用于已 live work |
| native record/scope grouping | 哪些 logical pass可共享 command/raster scope | 否，不改变 logical semantics |

## Exact-range pass culling

### Producer 模型

Buffer 使用确定性坐标压缩：

1. 收集一个 buffer全部 access的 `Offset` 和 `Offset + Size`。
2. 排序、去重，相邻边界形成半开 elementary intervals。
3. 每个 interval保存 latest producer pass或 imported-content sentinel。
4. access只遍历其覆盖 intervals。

Texture 使用 `(resource, mip, array layer, single aspect)` cell。D24 depth和 stencil必须是两个 producer cell；`Depth | Stencil` view要投影为两个 cell。

按原始 pass ordinal扫描时，必须先读取 prior producer，再更新 writer：

```text
for pass in recording order:
    for cell covered by access:
        if access reads or PriorContents is Required:
            prerequisites[pass].add(latestProducer[cell])

        if access writes:
            latestProducer[cell] = pass
```

这与现有 content contract保持一致：

- Read / ReadWrite需要旧 producer；
- pure Write + Discard不需要旧 producer；
- partial/preserve writer自身依赖旧 producer，后续 consumer只需引用这个 latest writer；
- full/discard overwrite切断旧 value edge，因此旧 writer可被裁掉；
- disjoint buffer interval、mip、layer、depth/stencil plane不互相保活。

### Roots

根集合严格包括：

- present；
- transient extraction；
- readback或结构化 completion publication；
- 每一个写 imported resource 的 pass；
- 具体、typed external interop operation。

当前 authoring surface只有 imported resource，因此可立即实现的 root是 imported write。完整交付还要补 present、extraction、readback和 interop observable declaration。不得把 `PassRecordingLane.Coordinator` 或“存在 execute delegate”当 root。

### Compiled output

Compiled plan至少保存：

- original pass ordinal bitset；
- `ActivePassOrdinals`；
- live resources、buffer views和 texture views masks；
- root/cull reason；
- live producer的一条确定性 retaining parent，用于解释链；
- declared/live/culled counts和 culled transient bytes。

保持 original ordinal比把 pass重新编号更稳：frozen payload、executor、attachments和 diagnostics仍按原 ordinal绑定；执行器只遍历 active ordinals。

resource/view realization、context lease、record、submit、external wait、final imported handoff和 cleanup都必须使用 live masks。dead transient没有 placement、dead view没有 native handle是正常状态。

### Cache 语义

Culling是 exact lowering，conservative和 optimized plan必须得到同一 live set。这样 cold miss与 cache hit不会改变 callback执行集合、profiling结构或 observable diagnostics。完整 frozen graph仍是 cache key；不要尝试删除 dead pass后与另一 graph共享 key。

root语义、producer规则或 culling debug option变化时必须 bump compiler semantic generation。紧凑 reason code始终随 plan缓存；需要日志时再用当前 frozen graph的名称格式化，不因开启 viewer而清空 cache。

## Compiled execution batch 与 record unit

建议的内部层级是：

```text
CompiledGraph
  LogicalPasses (original ordinals)
  ExecutionBatches
  RecordUnits
  PassToRecordUnit

ExecutionBatch
  Queue
  Waits / predecessor batches
  RecordUnits[]

RecordUnit
  LogicalPassOrdinals[]
  Standalone commands or RasterScope

RasterScope
  Extent / samples / layers
  Attachment set or union
  Ordered logical segments
  Beginning / boundary / ending semantics
```

logical pass不会因 merge消失：execute callback、pass-local resources、debug group和 profiler scope仍逐 pass存在。record unit只决定 command contexts、native begin/end、barrier归属和 submission batching。

Alias acquire是没有用户 callback的 internal execution batch。Raster merge则把多个相邻 logical pass放入一个 raster record unit或一个需要后端 suspend/resume的 batch。两者共同要求 submission不再硬编码为“一 pass一 submit”。

## Alias placement

### Eligibility 是精确证明

两个 resources只有同时满足以下条件才可共享一个 alias slot：

- 都是 graph-created、live、device-local transient；
- 不是 imported、extracted、protected、sparse、external-held或 requires-dedicated；
- backend allocation profile兼容；
- resource object可用 alias-capable creation contract建立；
- execution partial order证明 A的所有 uses先于 B的所有 uses，或反之；
- backend能建立新 owner的 alias acquire；
- new owner不读取旧 owner contents；
- D3D12 RT/DS等 native initialization要求可由 first use的 Clear/Discard/full Copy满足。

Format、descriptor、size或 usage flags不需要完全相同；是否能共享由 backend allocation profile决定。MSAA也不是一律禁止，只是常有不同 alignment/heap要求。

### Lifetime 使用 happens-before，不用 pass index

`lastPass(A) < firstPass(B)` 在 async compute/copy下不成立。execution DAG必须包含：

- live value/hazard dependencies；
- same native queue FIFO order；
- cross-queue waits/signals；
- queue ownership handoff；
- internal alias acquire batch。

精确定义：

```text
Before(A, B) = every use of A happens-before every use of B
```

实现可以用 transitive reachability bitsets，再把每个 resource压缩为 start/end frontier；测试 oracle保留 all-pairs定义交叉验证 frontier优化。两个 lifetime不可比较时绝不 alias，也不为节省 memory偷偷添加新的 queue serial edge。

### Placement heuristic

在已经安全的 candidate集合上使用 deterministic lifetime-aware best-fit decreasing：

1. 按 exact backend allocation class分区；dedicated resource绕过。
2. resource按 aligned size降序、alignment降序、stable resource ordinal升序排序。
3. alias slot只能接受与每个 occupant都 lifetime-comparable的 resource。
4. candidate cost是加入后 slot aligned capacity的增量；选最小值，tie按 stable slot id。
5. slot按 capacity/alignment排序，再放进留下最小 remainder的 compatible heap/page；tie按 page和 offset。
6. 同 slot occupants共享 exact offset，并按 lifetime顺序形成相邻 acquire chain `A→B→C`。
7. 任何 placement失败都 fallback到 no-alias placement，不能放宽 safety。

这是 heuristic，不是 optimal allocator。它只改变 memory placement，不改变 pass order、queue selection或 observable结果。

### Alias acquire batch

如果新 owner有多个互不支配的 first uses，把 barrier塞进任意一个 pass会错误串行其他 first uses。完整 IR应表达：

```text
A.EndFrontier[]
       ↓
internal alias-acquire batch
       ↓
B.StartFrontier[]
```

D3D12显式 lower alias barrier并执行必要的 Discard/Clear initialization；Vulkan lower memory dependency与 Undefined→目标 layout；Metal使用 placement ownership boundary与 fence/event；Null切换 active physical owner并 poison新 contents。

### Allocation profile

当前单个 opaque compatibility key方向正确，但 requirements需要扩展为 backend-owned profile，至少表达：

- concrete memory domain/type；
- heap/storage/cache properties；
- buffer/linear image/non-linear image/RT-DS granularity class；
- size、alignment、heap flags；
- alias-capable、requires/prefers dedicated；
- native maximum allocation/page constraints；
- buffer-image granularity等 backend restriction。

RG比较 opaque profile，不解释 DXGI/Vulkan/Metal枚举。

## Render-pass merging

### 第一个算法

只在 authoritative live sequence上做 left-to-right stable adjacent greedy：

1. 遇到 live raster pass开始 candidate scope。
2. 只检查紧邻的下一个 live pass。
3. candidate必须与整个 current scope兼容。
4. 兼容则 append；否则记录稳定的第一个 break reason并关闭 scope。
5. 不跨 non-raster、queue boundary、generic barrier、alias acquire、UAV barrier、copy、readback、present或中途 cross-queue wait。
6. 不用 profiler历史、wall clock、worker completion order或 vendor ID改变结果。

### 保守兼容矩阵

| 相邻 live pass 条件 | 结论 |
|---|---|
| 都是同 graphics queue raster | 必需 |
| extent、sample count、layer/view mask相同 | 必需 |
| exact same attachment views/ranges，后者 Load | 可 merge |
| 后者对已有 attachment Clear或Discard | break |
| A attachment被 B当普通 sampled texture | break |
| A attachment被 B显式 framebuffer-local read | capability-gated，后续扩展 |
| attachment set变化 | 第一个算法 break；attachment-union留待负载证据 |
| depth/stencil physical view变化 | break |
| plane read-only/write mode无法在 scope内表达 | break |
| format reinterpretation发生在 scope内 | portable break |
| alias/UAV/transition/copy barrier位于边界 | break |
| resolve只发生在 scope最后 ending access | 可 fuse |
| resolve后仍有当前 scope work | break |
| opaque/unsafe interop | break |
| marker/profile scope | 保留 logical marker；native不支持的 query才 break |

普通 SRV绝不能自动提升为 framebuffer-local read。D3D12 preserve-local与 Vulkan dynamic local read都需要 current-pixel/gutter承诺，因此未来必须有 explicit local-read binding/access和 shader reflection/annotation验证。

### 后端 lowering

| 情况 | D3D12 | Vulkan modern | fallback |
|---|---|---|---|
| exact attachment continuation | 一个长 Begin/End，或一次 submit中的 suspend/resume lists | 一个 dynamic rendering scope或 suspend/resume | standalone passes |
| pixel-local attachment read | 相邻 native passes + preserve-local | dynamic local read / by-region dependency | classic subpass或 standalone |
| ending resolve | ending access resolve | resolve attachment | standalone resolve |

当前只有 D3D12 backend，也仍应先生成 backend-neutral raster scope；capability不支持时 lower回现有 OMSetRenderTargets standalone行为。

### 公共 escape hatch

UE提供 `NeverMerge`，Unity unsafe pass也会形成边界，但 SomeEngine不应因此立刻增加通用 public boolean。具体 semantic已经足以机械 break：unsafe interop、local-read不兼容、query约束、queue/barrier boundary。先提供 engine/debug级全局 no-merge和完整 break reason；只有出现无法用具体语义表达的真实 consumer后，才讨论窄化 opt-out。

## CPU-visible placed resources

### 当前 API 不一致

`HeapDesc`允许 `Upload` / `Readback`，但 D3D12和 Null的 `CreatePlacedBuffer`拒绝非 DeviceLocal heap；`GetBufferRequirements`也不接受 memory type并始终报告 DeviceLocal。这使公共 API能创建一个不能容纳任何 portable resource的 CPU-visible heap。

### D3D12 与 portable 边界

D3D12允许 upload/readback heap上的 placed buffer，并规定 upload固定 `GENERIC_READ`、readback固定 `COPY_DEST`；upload/readback texture不允许。[D3D12 heap types](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_heap_type)、[CreatePlacedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createplacedresource)、[readback buffer](https://learn.microsoft.com/en-us/windows/win32/direct3d12/readback-data-using-heaps)

RHI应补齐的窄语义：

- `GetBufferRequirements(BufferDesc, MemoryType)`；
- Upload placed buffer：CPU write，GPU只读/copy source，native fixed read state；
- Readback placed buffer：GPU copy destination，CPU只在 exact completion后 read；
- Upload/Readback heap只接受 buffer；
- `CreatePlacedTexture`继续拒绝 CPU-visible heap；
- map/flush/invalidate仍由受控 API或 lease管理，不公开无同步的永久裸 pointer。

当前 `BufferUsage.Storage` 同时覆盖 read-only buffer view 和 writable storage/UAV view，无法表达“Upload buffer可被 shader只读、但绝不能 GPU write”。补 CPU-visible placement时必须把 resource-level shader-read与shader-write capability拆开，或引入等价的可验证 access capability；不能继续用一个 `Storage` bit同时决定 SRV和UAV合法性。

当前 abstract barrier compiler还必须理解 fixed-state CPU buffers：Upload的 vertex/index/constant/shader/copy-source用途都被 D3D12 `GENERIC_READ`覆盖，不应生成把 upload resource转出 fixed state的 barrier；Readback只允许 copy destination/明确 query output。

### 不进入 RG alias lifetime

当前 RG没有 host read/write pass、mapped lease、flush/invalidate或 CPU↔GPU happens-before。没有这些事实就无法证明 CPU-visible alias安全。因此：

- graph-created transient继续只用 DeviceLocal；
- Upload/Readback buffer由 transfer subsystem拥有并 import到 graph；
- RG只跟踪其 GPU access与 external readiness/completion；
- 不把 CPU-visible resource塞入 transient alias slot。

### 默认 transfer arena

小 buffer不应每个都建 placed object。D3D12 tiny buffer仍通常有 64 KiB placement alignment；默认设计应是少量长期存在的大 Upload/Readback page buffer，在 page内返回 `(BufferHandle, offset, size)` slice：

- Upload ring/linear slice在 submission读取前由 CPU写入；
- Readback slice在 exact queue completion后读取；
- slice只在 completion后复用；
- Vulkan future backend按 non-coherent atom flush/invalidate；
- 大型独立 CPU-visible buffer pool可使用 placed buffer作为 RHI完整性能力。

## MSAA texture 到 linear buffer

### 直接 copy 必须继续失败

Vulkan image→buffer copy明确要求 source sample count为 1。[vkCmdCopyImageToBuffer](https://registry.khronos.org/vulkan/specs/latest/man/html/vkCmdCopyImageToBuffer.html)

D3D12 `CopyTextureRegion` 对 multisampled resource要求复制完整 subresource且 source/destination sample count一致；linear buffer footprint不能成为匹配的 multisampled destination。[CopyTextureRegion](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-copytextureregion)

因此 portable路径只能是：

```text
MSAA texture
    → explicit resolve
single-sample device-local texture
    → texture-to-buffer copy
Readback buffer
    → wait exact GPU completion
CPU read
```

`CopyTextureToBuffer`不得隐式创建临时 texture、猜 resolve mode或添加隐藏 semantic pass。

### RHI/RG resolve contract

RHI增加：

```text
ResourceState.ResolveSource
ResourceState.ResolveDestination
ResolveMode: Average | Minimum | Maximum | SampleZero
TextureResolveRegion
ICommandContext.ResolveTexture(...)
```

RG增加对应 source/destination access/use，source必须 multisampled、destination必须 single-sample，range/aspect/extent/format compatibility在 Freeze/compile验证。standalone resolve必须在 raster scope外。

D3D12 `ResolveSubresource`要求 source/destination进入 ResolveSource/ResolveDestination，destination是 device-local single-sample resource。[D3D12 ResolveSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-resolvesubresource)

Vulkan color resolve同样要求 source sample count >1、destination sample count=1，并要求相同 format。[vkCmdResolveImage](https://registry.khronos.org/vulkan/specs/latest/man/html/vkCmdResolveImage.html)

不要提供语义模糊的 `Auto` resolve mode。portable fixed-function minimum先限定 normalized/floating color `Average`；integer color、depth和 stencil mode按 backend capability显式开放，无法统一时由 renderer helper选择明确 shader resolve pass。

### Attachment-integrated resolve

Raster attachment declaration可显式携带 resolve destination和 mode。resolve destination是真正的 graph write和 cache-signature输入；compiler可把 standalone logical resolve融合到最终 raster ending access，但不能凭空添加 resolve。

Store inference分别回答：

- resolved single-sample destination是否被后续 consumer观察；
- unresolved MSAA source之后是否仍被读取/提取；
- scope ending应 Preserve、Discard还是 Resolve并 Preserve source。

## Render Graph texture view

### 当前缺口

现有 `TextureViewDesc`确实已有 `Format`，但当前只支持 exact resource format，也没有 view dimension。由 resource层数猜 dimension会把不同 shader ABI混为一谈：

- `Texture2D` 与只有一层的 `Texture2DArray`；
- `Texture2DArray` 与 `TextureCube/CubeArray`；
- `Texture2D` 与 `Texture2DMS`；
- 3D SRV与可以选择 W slice的 RTV/UAV。

### Resource creation contract

Cube和 format reinterpretation必须在 resource创建前声明：

- Vulkan cube view要求 `VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT`；
- Vulkan alternate view format要求 mutable-format和 compatible format，若提供 format list则 view必须在列表中；
- D3D12 alternate typed views通常要求对应 typeless base resource。[VkImageViewCreateInfo](https://docs.vulkan.org/refpages/latest/refpages/source/VkImageViewCreateInfo.html)、[D3D12 SRV desc](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_shader_resource_view_desc)

Texture resource descriptor因此需要表达：

```text
Resource dimension: 1D | 2D | 3D
Cube-compatible creation intent
Immutable allowed view-format set
```

allowed formats比公开无约束 `MutableFormat` bit更安全：默认集合只有 storage format；需要 linear/sRGB pair时显式加入兼容格式。backend由集合选择 D3D typeless base或 Vulkan mutable format/list。排序去重后的集合、cube compatibility和 resolved resource dimension都进入 canonical data与 allocation requirements。

Depth/stencil plane view仍由 resource depth format + single aspect表达；D32 depth SRV和 D24 depth/stencil SRV的 backend typed mapping不要求用户把公共 format改写成内部 color format。

### View contract

Freeze后的 view必须拥有确定 dimension，不保留模糊 `Auto`：

```text
1D / 1DArray
2D / 2DArray
2DMS / 2DMSArray
Cube / CubeArray
3D
```

主要规则：

| View | 必要规则 |
|---|---|
| 2D | portable baseline要求 non-array 2D resource；single sample |
| 2DArray | one or more layers；single sample；一层也不自动降成 2D |
| 2DMS / 2DMSArray | resource sample count >1；mip 0 only；不能 storage/cube/3D |
| Cube | 2D cube-compatible、square、exact 6 layers、first layer按 cube边界 |
| CubeArray | layer count为 6的倍数，shader feature/capability支持 |
| 3D | 3D resource；portable SRV看完整 W extent |
| 3D RTV/UAV slice | W slice是 view restriction，不是可独立 transition的 array subresource；用单独 slice range表达 |
| attachment | exact mip；layered attachment需要明确 layer/view mask语义 |

`TextureSubresourceRange.FirstLayer`不应继续同时表示 2D array layer和 3D W slice。array subresource range与 3D view slice range要分开；barrier仍按 native transitionable subresource工作。

### Format family

第一组 portable reinterpretation只开放实际验证过的 compatible family，例如 `R8G8B8A8UNorm ↔ R8G8B8A8UNormSrgb`。不接受“byte size相同所以可以 reinterpret”。每个 view usage还要做 format feature验证：sampled、storage、color attachment、depth/stencil attachment的支持并不等价。

### Shader contract

Slang reflection/artifact必须保留并验证：

- dimension；
- arrayed；
- multisampled；
- float/uint/sint/depth sample type；
- storage image format；
- local/input-attachment shape（若使用）。

用户 annotation补充 machine analysis难以证明的 framebuffer locality等 effect，但不能覆盖 Slang已知的 texture shape。否则 `TextureCube`绑定 2DArray view、`Texture2DMS`绑定 single-sample view等错误仍会延迟到 native draw。

## Capability snapshot 与透明 cache

`DeviceCompilationSnapshot`需要增加 backend-neutral能力，而不是让 RG读取 D3D12 tier/Vulkan extension枚举：

- native raster scope；
- suspend/resume；
- attachment continuation和 local attachment read；
- ending resolve；
- independent depth/stencil resolve与支持 modes；
- read-only depth/stencil；
- layered/secondary recording；
- max color attachments、attachment union、raster segments；
- cube array、mutable format与 supported view families；
- alias-capable allocation、granularity、dedicated/page constraints。

这些 capability、compiler policy version和 allocation profile semantic generation必须参与 exact cache environment。plan保存 relative slot/offset、active ordinals、record units、scope boundaries和 barrier templates，不保存 physical heap/resource/view、descriptor、fence或 command context。

每次改变以下任何规则都必须 bump compiler/device semantic generation：

- root/value-producer语义；
- alias compatibility或 placement heuristic；
- raster merge eligibility；
- resolve mode/format capability；
- view dimension/format-family lowering。

## 实现依赖顺序

这不是“临时兼容版本”列表；每项都按最终结构落地，依赖关系如下：

1. 补齐 explicit resolve、view shape/format set、CPU-visible buffer requirements和 backend-neutral capability facts。
2. 将 validation/normalization从 queue/hazard compiler中分离，构造 exact value-producer IR。
3. 生成 authoritative live pass/resource/view sets，并让 conservative/optimized plan共享它。
4. 用 execution batches和 record units替换执行器的一 pass一 submit假设；standalone lowering必须保持当前输出。
5. 只在 live graph上重建 queue dependencies、barriers、external waits和 final handoff。
6. 建立 execution DAG reachability、resource frontiers和 backend allocation profile。
7. 增加 Null active-alias owner/poison oracle和 internal alias-acquire batch。
8. 实现 no-alias baseline与 deterministic alias placement的并列输出、metrics和 fallback；真实负载确认前保持 placement优化关闭。
9. 增加 raster candidate grouping、break reason、store/resolve inference和 standalone/native lowering；真实负载确认前保持 merge优化关闭。
10. 最后才考虑 attachment-union、framebuffer-local read、跨 command-list suspend/resume收益策略、pass reorder或 vendor-specific policy。

## 验证矩阵

### Culling

- dead transient-only branch不执行、不分配、不建 view、不租 context；
- imported write自动 root，imported read-only不自动 root；
- full/discard overwrite裁掉旧 writer；partial/preserve与 ReadWrite保留 prior producer；
- disjoint buffer interval独立；跨 interval read保留多个 writers；
- mip/layer/depth/stencil plane独立；
- coordinator lane和任意 host variable mutation不影响 live set；
- 零 root graph产生零 submit；
- cache miss/hit、conservative/optimized live set一致；
- dump和 retaining chain重复编译 byte-for-byte稳定。

### Alias

- sequential buffer/texture共享 exact offset；incomparable async lifetimes不共享；
- transitive cross-queue happens-before可共享；
- same-queue FIFO edge参与证明；
- incompatible profile、dedicated、imported、CPU-visible不共享；
- `A→B→C`只产生相邻 acquire chain；
- multiple start frontiers通过 internal batch激活而不互相串行；
- RT/DS acquire后 Clear/Discard/full Copy；
- Null对 missing barrier、inactive owner access和 undefined read失败；
- D3D12 WARP InfoQueue无 Error/Corruption；
- randomized DAG property test证明 simultaneously-live physical ranges不重叠。

### Raster merge

- same attachment + Load形成 candidate scope；
- Clear/Discard、ordinary sample、queue change、alias/UAV barrier、copy和 resolve-in-middle逐一 break；
- culling后新相邻 pass可以成为 candidate；
- logical debug/profiler identity完整保留；
- merge off/on output逐字节一致；
- ending resolve后 single-sample readback正确；
- capability变化导致 cache miss；
- D3D12 standalone与合并后的单个 `BeginRendering`/`EndRendering` OM scope由 WARP验证；native `BeginRenderPass`与 suspend/resume尚未实现，仍是未来 capability-gated lowering。

### Views / resolve / CPU-visible buffers

- 2D、Array、Cube、CubeArray、2DMS、2DMSArray、3D矩阵；
- linear/sRGB allowed family成功，未声明/不兼容 family失败；
- shader dimension/sample type mismatch在 Freeze或 binding validation失败；
- MSAA→buffer在 common RHI validation提前失败；
- explicit resolve→single-sample→readback内容正确；
- Upload/Readback placed buffer可创建、CPU access正确、fixed-state非法用途失败；
- CPU-visible placed texture始终失败；
- transfer slice在 exact completion前不可复用；
- D3D12 persistent CPU descriptor跨页内共享、native view retirement后slot复用；
- buffer/texture requirements对仅 debug name不同的 descriptor只做一次native query；
- 3D odd-height texture copy使用512-byte对齐slice pitch并通过WARP readback。

### 2026-07-11 实际验证结果

- `SomeEngine.Graphics.Tests`：32/32 通过；
- `SomeEngine.Graphics.Direct3D12.Tests`：34/34 通过，使用 WARP 与 D3D12 debug/InfoQueue 验证；
- `SomeEngine.Assets.Tests`：104/104 通过，包含实际 Slang cook、artifact codec 与正式 shader projection；
- `SomeEngine.RenderGraph.Tests`：111/111 通过；
- 聚焦矩阵合计 281/281 通过；
- `SomeEngine.slnx` 构建过程中，Graphics、Null、D3D12、Assets、AssetCook、Render、RenderGraph、RenderGraph Sample 及其相关测试项目均成功编译；解决方案最终仅在并行 ECS 工作树的 `TopologyFinalizerAndHierarchyPropagationTests.cs:466` 失败，该调用缺少 `HierarchyPropagationPartitionProof` 新增的 `structureEpoch` 参数，与本报告范围内的 RG/RHI 改动无依赖关系。

## 指标与启用门槛

### Culling

必须报告 declared/live/culled pass、live/culled resources/views、culled transient bytes、value-flow build time、live-only downstream compile time和 root/reason histogram。Culling是 exact optimization；通过 correctness和 determinism gate后不等待 gameplay收益调参。

### Alias placement

同一 frozen workload在 alias off/on且 scheduling完全相同的条件下报告：

```text
LogicalRequestedBytes
NonAliasedPlacedBytes
AliasSlotCapacityBytes
PlannedHeapBytes
TemporalReuseBytes
AlignmentPaddingBytes
AliasSavingsBytes
```

同时报告 eligibility exclusion reason、slot/chain distribution、alias barriers、cross-queue acquire batches、compile time、heap/resource creation time、GPU time和 memory watermark。没有 transient resource-heap persistent page pool时不要伪造传统 external fragmentation；此时主要事实是 temporal reuse、alignment padding和 heap slack。

没有 representative game workload，所以本报告满足算法公开和 correctness设计，但没有满足“默认启用 alias placement”的收益门槛。

### Render-pass merging

必须报告 live raster passes、merge candidates、merged logical passes、record units/native scopes、loads/stores elided、fused resolves、suspend/resume boundaries、break-reason histogram、compile time、parallel recording CPU time、GPU frame/pass time和 tile/off-chip traffic（平台可测时）。

没有 representative game workload和 tile backend，所以本报告不支持现在声称 merge能提高真实游戏性能，也不支持默认开启 attachment-union、local-read或重排策略。

## 最终实施判断

已经实现且不需要等待负载 gate 的内容：

- exact-range culling及其 diagnostics；
- conservative/optimized统一 live set；
- execution batch / record-unit结构与 standalone lowering；
- explicit resolve command/state/RG access和 attachment resolve；
- common-layer MSAA linear-copy拒绝；
- explicit view dimension、cube-compatible resource和 allowed view formats；
- shader texture shape validation；
- CPU-visible placed buffer的窄 RHI能力与 fixed-state validation；
- transfer arena边界；
- native capability snapshot、metrics和 debug no-cull/no-alias/no-merge模式。
- transparent exact compilation cache、CPU-only plan lease与conditional optimized flight；
- D3D12 persistent CPU descriptor page pool与name-insensitive allocation-requirements cache；
- shader effect/operation双来源保留、Atomic admission和其余operation fail-close。

算法已经实现、但真实负载确认前不应默认开启：

- lifetime-aware alias placement；
- stable adjacent raster merge。

继续推迟：

- 为了 memory或 merge主动改变 queue/schedule；
- pass reorder；
- attachment-union merge；
- framebuffer-local SRV/UAV自动策略；
- vendor-specific阈值或黑白名单；
- fixed-function与 shader resolve的隐式 `Auto` 选择；
- native BeginRenderPass/suspend-resume性能 lowering；
- graph-created CPU-visible transient，直到 RG能表达 host access和 CPU↔GPU happens-before。

## 参考资料

- [UE Render Dependency Graph](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine)
- [UE `ERDGPassFlags`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/ERDGPassFlags?lang=en-US)
- [Unity NativePassCompiler](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Compiler/NativePassCompiler.cs)
- [Unity Render Graph Viewer](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-viewer-reference.html)
- [Filament FrameGraph](https://google.github.io/filament/notes/framegraph.html)
- [D3D12 placed resources and aliasing](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createplacedresource)
- [D3D12 resource barriers](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12)
- [D3D12 native render passes](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist4-beginrenderpass)
- [D3D12 resolve](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-resolvesubresource)
- [D3D12 texture copy](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-copytextureregion)
- [D3D12 texture views](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_shader_resource_view_desc)
- [Vulkan memory aliasing](https://docs.vulkan.org/spec/latest/chapters/resources.html#resources-memory-aliasing)
- [Vulkan image views](https://docs.vulkan.org/refpages/latest/refpages/source/VkImageViewCreateInfo.html)
- [Vulkan image resolve](https://registry.khronos.org/vulkan/specs/latest/man/html/vkCmdResolveImage.html)
- [Vulkan image-to-buffer copy](https://registry.khronos.org/vulkan/specs/latest/man/html/vkCmdCopyImageToBuffer.html)

See also [[Render-Graph]].
