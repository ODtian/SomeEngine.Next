# Render Graph

> Status: accepted architecture target, with an implementation snapshot updated 2026-07-12. The former imported checkpoint remains reference input, not a compatibility target.

SomeEngine Render Graph uses an Unreal-RDG-style, per-invocation recording model. Renderer control flow records the graph that actually exists, the recording is frozen once, and the graph is compiled, culled, scheduled, and executed for that invocation. Bindless changes how a declared resource reaches a shader; it does not create a second dependency model.

## Ownership boundary

The Render Graph owns:

- graph-scoped texture, buffer, and acceleration-structure identities;
- immutable graph views and pass-local access declarations;
- pass setup metadata and execute callbacks;
- resource dependency and content-validity analysis;
- pass culling and deterministic ordering;
- queue synchronization and abstract barrier intents;
- transient lifetime intervals and alias eligibility;
- persistent resource ownership and temporal history-ring mechanics across successful immediate invocations;
- completion-gated ownership transfer for explicitly exported graph-created resources;
- attachment lowering and native render-pass merging;
- canonical freeze/signature construction, the transparent in-memory compilation cache, and immutable-plan publication and retirement;
- deterministic structural capture plus the explicitly supported executable replay command subset;
- diagnostics explaining every dependency, barrier, cull, and alias decision.

The Render Graph does not own:

- renderer history policy, camera-cut decisions, resize resampling algorithms, or semantic history migration passes;
- physical resource creation APIs, descriptor heaps, view caches, residency, streaming, or deferred destruction;
- the global bindless heap or stable descriptor indices;
- shader compilation, pipeline compilation, pipeline readiness policy, or pipeline caches;
- RenderWorld extraction, material selection, quality fallback, or feature scheduling;
- semantic GPU algorithms such as mip generation, history migration, resolve selection, sparse-page policy, or fallback rendering;
- device-loss reconstruction or universal resource-content/command replay beyond the capture schema's declared portable subset.

Those systems may use the graph and provide state to it, but they do not become graph subsystems.

## Recording and execution

A recording and execution use one complete ownership pipeline:

1. Setup appends resources and passes in deterministic program order. Normal CPU control flow decides which passes exist.
2. Freeze waits for every setup result that can affect structure, validates the closed declaration, and produces an immutable canonical snapshot and signature.
3. After Freeze, the Render Coordinator drains and revalidates completed candidates at the normal plan-selection boundary, then an exact transparent-cache lookup binds a matching immutable compiled plan. On a miss, the invocation selects a deterministic conservative plan. It starts or joins a single-flight optimized compilation only when alias placement or raster merging can change lowering; with both policies disabled it launches no worker. It waits only when conservative lowering cannot satisfy a hard capability or memory constraint; the coordinator then validates and binds that exact result at a required join boundary before recording begins.
4. The invocation binds its own payloads, imported resources, external waits, clear values, descriptors, and physical allocations to the selected plan.
5. Execute callbacks record commands using only pass-local capabilities; the Render Coordinator or its submission executor stitches and submits work in compiled order.

The public recording surface is a single-writer, ordered builder. Arbitrary threads never mutate the same graph registry. Parallel feature jobs may prepare immutable inputs, and pass-private setup tasks may run after the pass receives a stable ordinal; a task that affects the signature must complete before Freeze, while payload-only work may overlap compilation but must complete before its pass records commands.

The frozen snapshot, Compiled Graph Plan, and per-invocation data are separate objects with separate lifetimes. Cache lookup or publication never exposes a persistent public graph identity.

Execute callbacks are command-recording functions, not general deferred application callbacks. They must not:

- create or import graph resources;
- add passes or expand declared ranges;
- select an undeclared shader or pipeline;
- resolve a graph resource through a global lookup;
- rely on host side effects for correctness or pass liveness.

The normal per-invocation recording model must not be confused with UE's debug `r.RDG.ImmediateMode`, which bypasses graph optimizations and executes at `AddPass` for debugging.

## Identity model

Four identities remain distinct:

| Identity | Meaning | Lifetime |
|---|---|---|
| Physical graphics resource | Backend-owned texture, buffer, or acceleration structure | Graphics resource system and GPU completion |
| Graph Resource | One logical resource created or imported into one recording | One recording |
| Graph View | One immutable, exact range and interpretation of a Graph Resource | One recording or one pass declaration |
| Pass Access | One pass-local permission to use a Graph View with a closed effect and content contract | One pass setup/execute pair |

`TextureId`, `BufferId`, and `AccelerationStructureId` denote Graph Resources because the corresponding `TextureHandle`, `BufferHandle`, and `AccelerationStructureHandle` names already denote physical Graphics resources in SomeEngine. A write does not return a new public Graph Resource identity.

The compiler may create internal producer epochs for individual ranges so dead writes can be removed and reads can select the correct producer. Those epochs are not public handles and never cause implicit physical rename or copy. Code that needs old and new contents concurrently creates two resources and declares the copy or ping-pong explicitly.

A Graph View contains only:

- its parent Graph Resource identity;
- its exact texture mip/layer/plane/aspect range, buffer byte range, or whole acceleration-structure object;
- view interpretation required for compatibility, such as format and view kind.

Access effects, prior-content requirements, write coverage, binding slots, descriptor indices, and backend state are not properties of a Graph View.

## Resource classes

Graph-created resources default to `Transient`, but `BufferResourceDesc` and `TextureResourceDesc` may explicitly select `Persistent` or `Temporal`. A transient removed by culling is never allocated. Persistent and temporal resources are owned by the graph's continuity store and keyed by their stable declaration identity; they do not introduce a retained public graph, template, or second compiler.

A temporal resource owns `HistoryCount + 1` physical slots. The current `TextureId`/`BufferId` is the writable slot and `History(framesAgo)` is a read-only prior slot. Frame advancement and new-content publication happen only after successful submission. Descriptor or history-count changes replace the ring, stale generations are never published, and `ResetHistory` invalidates history explicitly. The renderer still decides when a camera cut, resize, or quality change calls for reset or an explicit migration/resampling pass.

Imported resources are external physical resources temporarily represented by an ordinary Graph Resource. Import does not make their graph accesses implicit or untracked. An import contract supplies:

- physical identity and generation;
- initial abstract access and queue ownership;
- initial content validity for the imported range;
- any external completion dependency required before graph access;
- lifetime coverage through the graph's GPU completion;
- required return access and queue ownership;
- a completion publication target through which the external owner learns that the return contract is established.

Import is an explicit external-visibility promise. Any declared write to an imported resource is an Observable Graph Output and roots its producers; an imported resource is not a discardable scratch allocation. If external contents do not matter, the renderer creates a graph transient instead. After the last live use, the graph establishes the import's return access and queue ownership and publishes completion to the external owner. This rule applies even when the resource will be imported again by a later recording.

An acquired swapchain backbuffer uses the same import mechanism with explicit backend-neutral `ResourceState.Present` initial and final boundaries. The graph may transition it to an attachment/copy state and must return every touched and untouched subresource to `Present`; only after the graph completion is observed does the external swapchain owner call `Present`. Render Graph does not acquire, resize, or own the swapchain itself.

Within one recording, `(physical identity, generation)` is canonical. Re-importing the same physical object returns the same Graph Resource only when descriptor, initial state, content validity, return contract, and external wait agree; otherwise recording fails. Different views of one physical object are created as Graph Views of that canonical import, not as separate imports.

The Graphics layer must also report physical allocation identity and byte/subresource overlap for externally aliased objects. Simultaneously importing overlapping external aliases as independent resources is rejected unless the backend contract proves the imported ranges are physically disjoint. Render Graph never assumes that different object handles imply different memory.

Export applies to a transient graph-created resource and is an explicit Observable Graph Output. `GraphBuilder.Export` roots its producer, requests a final abstract access and queue contract, and `GraphExecution.Exports` transfers the physical handle only after every producing completion has been published. Failed execution never transfers ownership, and a successful export can be imported later with its exact completion and final state. Import return and transient export are therefore distinct operations: one returns an existing external object, while the other transfers a graph-created object outward.

Resource lifetime is an explicit closed enum (`Transient`, `Persistent`, `Temporal`) and temporal history count is part of the canonical resource contract. Resize/history migration remains an explicit renderer decision; bindless remains a descriptor projection; export remains an operation rather than a creation flag; and aliasing remains a compiler decision used only when safety is proven.

## Pass Access model

Every shader, attachment, copy, resolve, indirect, acceleration-structure, readback, and external operation lowers to one internal access record:

```text
resource identity
exact view/range
possible resource effect
prior-content requirement
write coverage
pass execution domain
shader/non-shader provenance
declaration and pass ordinals
```

The renderer declares intent, not Vulkan layouts, D3D12 states, pipeline-stage masks, or queue-family barriers. Shader stage and pipeline information come from the frozen shader/pipeline artifacts; copy, attachment, present, and indirect operations have intrinsic execution domains.

Texture dependencies are tracked per mip, array layer, and plane/aspect. A 3D texture depth slice may be a view restriction but is not a separately transitionable texture subresource when the backend cannot represent it.

Buffer dependencies are tracked by exact byte range. This deliberately exceeds UE RDG's public whole-buffer access model. A backend that cannot transition ranges independently may conservatively widen state transitions or serialize incompatible states, without weakening graph-level overlap validation.

Acceleration structures are first-class whole-object Graph Resources rather than invisible descriptors. Build, update, compact, copy, and ray-query/trace accesses name an `AccelerationStructureId`; geometry, instance, scratch, indirect, and shader-table storage remain exact Buffer Graph Views. A backend's AS backing allocation participates in physical identity, lifetime, alias, and barrier validation even when the native API exposes a separate acceleration-structure object.

Overlapping accesses in one pass must be represented by one combined access envelope. The graph schedules only pass boundaries. Algorithms that require an intra-pass memory dependency either split into passes or issue a pass-local abstract memory barrier over an already-declared access; they cannot introduce a new resource at that point.

A pass-local abstract memory barrier is deliberately narrow. It may order memory effects only within the same pass, selected queue, and execution domain, over a subset of an already-declared access envelope. It may not transition a layout or abstract state, transfer queue ownership, acquire an alias, widen a range or effect, introduce a descriptor, or bridge undeclared raster/compute/copy domains. It lowers only to an appropriate same-queue memory/UAV ordering primitive; every other dependency requires a pass boundary.

## Resource effects and Slang

Shader build artifacts preserve two parallel facts for every resource binding:

- the complete Slang-reported resource access/type information;
- the optional explicit declaration-local `[ResourceEffect(...)]` authored on the Slang resource parameter or ParameterBlock leaf.

The shader-library prelude defines one Slang user attribute whose reflected signature is conceptually:

```slang
[__AttributeUsage(_AttributeTargets.Var | _AttributeTargets.Param)]
struct ResourceEffectAttribute
{
    ResourceEffects effects;       // Read, Write, or ReadWrite
    ResourceOperations operations; // flag set: None, Atomic, Append, Consume, RasterOrdered, Feedback
};

[ResourceEffect(ResourceEffects.Read, ResourceOperations.None)]
Texture2D<float4> inputColor;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceOperations.Atomic)]
RWStructuredBuffer<uint> counters;
```

`ResourceEffectAttribute` is the Slang definition behind source spelling `[ResourceEffect(...)]`; the `Attribute` suffix is not written at use sites. Exactly zero or one attribute is permitted on each resource declaration. It may appear on a global resource parameter, entry-point resource parameter, or resource-valued leaf field recursively reached through a `ParameterBlock`. It is rejected on an entry-point function, a container as a whole, a sampler, or a string-based list of binding names. If a shared declaration is used by several entry points, its effect is the exhaustive union for that declaration; shader authors use a more specific declaration when they require a narrower contract.

Shader compilation reads the attribute from Slang `VariableReflection`, preserves its normalized effect and operation arguments in the shader artifact, and independently preserves Slang's resource shape/access information. The current artifact does not claim to preserve source locations. A toolchain upgrade must pass attribute-reflection and ParameterBlock-recursion compatibility tests before its shader artifacts are accepted.

The explicit declaration is an exhaustive may-effect contract when present. Slang information is never discarded or demoted to an invisible fallback. The resolved effect must fit the Slang type capability; for example, an explicit write through a read-only Slang resource type is a shader build error. When no explicit effect exists, the Slang capability is used conservatively.

The resolved effect keeps independent dimensions:

- base `Read` and `Write` effects;
- operation qualifiers such as `Atomic`, `Append`, `Consume`, raster ordering, and feedback;
- content guarantees, which remain pass-binding facts rather than shader type facts;
- provenance for both the authored declaration and Slang reflection.

An `RWBuffer`-like Slang type does not prove that a particular shader body reads and writes. We do not parse DXIL, SPIR-V, or Metal IR, and stock Slang has no supported final-IR effect query that would justify such parsers. Explicit effects supply the body-level intent that reflection cannot reliably recover.

Pass setup freezes the possible shader/pipeline set and its aggregate effect envelope. Execute may choose only within that closed set. A compiled shader permutation may remove declared resources and thereby narrow the envelope before graph compilation; execute can never expand it.

Shader effects validate a Pass Access but do not identify its resource. A descriptor binding, bindless index, or scalar handle still needs a graph access declaration whenever the referenced resource is graph-managed.

Operation qualifiers refine, but do not replace, base effects:

| Qualifier | Required/derived base effect | Additional graph meaning |
|---|---|---|
| `Atomic` | conservative fallback is `Read + Write` | read-modify-write ordering over the exact storage range |
| `Append` | `Write` on element storage | safe lowering would require its counter as a distinct `Read + Write + Atomic` access |
| `Consume` | `Read` on element storage | safe lowering would require its counter as a distinct `Read + Write + Atomic` access |
| raster ordered | must include `Write` | ordered memory semantics in the supported raster domain |
| feedback | `Read + Write` | simultaneous feedback-loop ordering/layout requirements |

The artifact preserves declared and reflected operation qualifiers even when Render Graph cannot yet lower them. The current safe graph path admits `Atomic` only when the binding is exact read-write storage and fail-closes `Append`, `Consume`, raster ordered, and feedback operations. Future append/consume support must materialize the implicit counter as a separately tracked access rather than pretending the element binding is sufficient. Qualifier/type mismatches are shader-build errors, not runtime fallback choices.

## Content contract

Content semantics are orthogonal to resource effects.

For a general Pass Access:

- `PriorContents.Preserve` means the accessed range may depend on valid previous contents;
- `PriorContents.Discard` means previous contents are not semantically consumed;
- `WriteCoverage.Unknown` means the graph cannot assume every addressed element is written;
- `WriteCoverage.Full` asserts complete coverage of the exact declared view/range.

The safe default is `Preserve + Unknown`. `FullOverwrite` is derived from write effect, discarded prior contents, and full coverage; it is not a separate user effect.

New transient resources begin with undefined contents. Imports explicitly state whether their contents are valid. Any read requires a valid producer, clear, or valid import. A read combined with `PriorContents.Discard` on the same overlapping range is invalid, except that an attachment clear is a graph-visible operation with its own content semantics.

Discard removes an old-content dependency. It does not remove earlier-reader ordering, queue ownership, alias acquisition, or memory-order dependencies.

### Access normalization and join algebra

Before dependency construction, all accesses to one canonical Graph Resource are partitioned into non-overlapping elementary cells using every texture mip/layer/plane/aspect boundary or buffer interval endpoint mentioned by the pass; an acceleration structure is one whole-object cell. Every declaration, shader candidate, aliasing binding, attachment operation, and non-shader operation is projected onto those cells. Each cell then has exactly one normalized pass envelope.

The join is normative:

- possible base effects and operation qualifiers are set union;
- provenance is retained as a set, never overwritten by the winning conservative fact;
- `PriorContents.Preserve` dominates between coexisting write declarations; `Discard` survives only when every possible path discards prior contents;
- any possible `Read` combined with `Discard` on the same cell is invalid, except an attachment `Clear`, which is modeled as an ordered initialization before the pass body rather than as a read-discard access;
- write coverage is a must-guarantee: across mutually exclusive shader/pipeline candidates it is `Full` only if every candidate fully covers the cell; across operations that definitely co-execute, the exact union of `Full` ranges may prove the cell fully covered; otherwise it is `Unknown`;
- an `Atomic` storage access and a materialized append/consume counter preserve their prior contents; append element storage may discard old data but has unknown coverage unless a separate mechanical proof covers the exact range;
- `FullOverwrite` holds for a cell only when it has `Write`, has no possible `Read`, discards prior contents, and has mechanically proven full coverage.

These rules are applied after canonical import identity and physical-alias validation, so two bindings cannot evade the join merely by naming the same storage through different handles.

## Raster attachments

An attachment is a specialized Pass Access in the same dependency system, not a parallel graph mechanism. A color or depth/stencil attachment records its exact view, role, required load operation, clear value where applicable, resolve target where explicitly requested, and depth/stencil read/write effects.

The load operation is mandatory:

- `Load` requires valid prior contents;
- `Clear` creates graph-visible initialized contents;
- `Discard` does not preserve prior contents.

Store behavior is inferred from subsequent live consumers, present, extraction, external visibility, and native pass fusion. It is not a public pass option. Resolve destination and resolve mode are explicit because they change semantic output; the compiler may infer whether the multisampled source itself needs storing afterward.

The graph may merge compatible adjacent logical raster passes into one native render pass or subpass sequence. Merging must preserve logical pass identity, profiling scopes, attachment contents, and all externally observable behavior.

Grouping is a canonical, stable scan over the final logical pass order. Starting with the leftmost ungrouped raster pass, the compiler adds only the immediately following pass and only when that candidate is compatible with every attachment and invariant of the entire current group; otherwise it closes the group and starts a new one. It never reorders passes to create a merge. The no-merge result is the correctness fallback. Barrier intents are sorted by physical identity/generation, queue pair, access pair, and range start, and only adjacent ranges with identical lowering requirements may coalesce.

The compiler never infers a missing mip-generation, resize-migration, fallback-shader, clear, copy, or resolve algorithm merely because a later read lacks a producer. Semantic GPU work is added explicitly by the renderer or a feature helper. The compiler may insert only synchronization and backend-lowering commands.

## Bindless boundary

Bindless is a Graphics/RHI binding transport. It is not a Graph Resource kind, a graph-wide access domain, or a replacement for a Pass Access.

For any graph-managed texture, buffer, or acceleration structure:

```text
Graph View + Pass Access
        ↓ dependency, lifetime, barrier
physical view resolution
        ↓ binding projection
ordinary binding or bindless descriptor index
```

The same access record is used regardless of the final binding projection. Passing only an integer descriptor index to execute cannot make the graph discover the resource afterward.

The global material bindless heap is permitted outside per-resource graph tracking only under one engine-level contract:

- every referenced resource is resident and kept alive through execution;
- descriptor-to-resource mapping is stable for the execution interval;
- resources remain in a compatible shader-read state;
- the heap contains no hidden graph transient or writable resource;
- streaming, replacement, and destruction synchronize outside the graph.

The Graphics resource system proves that contract with an opaque, non-forgeable resident-read-only heap lease, not with an integer supplied by renderer code. The lease identifies device and heap generation, keeps descriptor mappings and their resources alive through graph GPU completion, carries any readiness dependency, and exposes typed slot-generation validation. Freeze records the lease required by the closed shader candidate set; execute can obtain a raw shader index only through that frozen lease. GPU-generated indices may select any entry covered by the lease, but the heap class remains read-only and graph-external.

In development builds, Graphics retains a reverse map from `(heap generation, slot, slot generation)` to physical resource identity, resource generation, and capability class. This permits diagnosis of stale slots and rejects transient or writable graph resources entering the resident-read-only heap. Slot reuse occurs only after the lease and associated GPU completion retire. These checks validate the external contract; they do not turn the heap into a Graph Resource or infer per-index barriers.

There is no `BindlessDomain` Graph Resource and no `UseAllBindlessResources` operation. A persistent resource that must be written, transitioned, aliased, or synchronized by the graph is imported and declared normally.

Unity's `UseAllGlobalTextures` is not an exception: its current public implementation enumerates the finite set of concrete global `TextureHandle` values already known to that graph and adds ordinary read accesses. It is a conservative hidden-binding fallback, not a descriptor-index or bindless-heap dependency model.

Within the public sources surveyed here, we found no mechanism in UE RDG, Unity RenderGraph, Dagor daFrameGraph, Daxa TaskGraph, O3DE Atom, Falcor, Granite, or Frostbite's published FrameGraph that derives concrete graph resources from a shader's runtime descriptor index. This is a bounded survey result, not a claim about undisclosed engine internals. A whole-heap or may-use set is at most a residency/validation contract and cannot replace exact graph accesses.

Taken together, the surveyed public UE, Dagor, and Daxa material supports this split; it does not establish anything about undisclosed engine paths:

- UE's public metadata-driven bindless helper receives parameter metadata and parameter data. Combined with RDG's ordinary parameter-declaration model, this supports the inference that the helper projects already-known bindings; the helper alone does not prove that every internal UE bindless path has exact RDG declarations.
- Dagor attaches `.bindlessShaderVar(...)` to an already concrete `registry.read/modify(...).texture/buffer().atStage(...)` request. Material textures use a separate material-system-owned global bindless table.
- Daxa shaders receive image IDs and device addresses directly, while resources needing runtime synchronization remain explicit task attachments. Its tutorial provides a concrete example that leaves an initialized, permanently read-only vertex buffer outside TaskGraph tracking; that example is a precedent, not a complete formal safety specification for every Daxa resource.

Snowdrop's published D3D12 memory-management failure supplies the inverse AAA evidence: descriptor indices stored in GPU buffers were invisible to CPU residency tracking, so the engine had to provide a conservative may-use residency set. Such a set prevents eviction but still does not provide per-resource access, layout, range, queue, lifetime, or alias information to a Render Graph.

## Dependency and culling semantics

Setup append order supplies the ordering of conflicting accesses. After culling, the canonical logical order is a stable topological order that selects the smallest original recording ordinal whenever multiple passes are ready. Conflicting accesses therefore preserve their program-observable recording order, and independent work is not speculatively reordered merely to chase an unmeasured scheduling benefit. Queue submissions may overlap this logical order only where the dependency graph proves independence.

The compiler maintains internal producer state per exact range and derives:

- read-after-write dependencies;
- write-after-read dependencies;
- write-after-write dependencies;
- queue-ownership dependencies;
- memory-order dependencies for UAV/atomic/append/consume-style effects;
- alias acquisition dependencies;
- external wait and final-state dependencies.

Culling starts from Observable Graph Outputs and walks producers backward. Roots include:

- presentation;
- extraction;
- readback and completion callbacks;
- any write to an imported resource, whose import contract publishes the returned external contents and state;
- an explicit host or external-system side effect.

An execute lambda's arbitrary CPU mutation is never an implicit root. The normal API has no generic `NeverCull`, `HasSideEffect`, or `AllowGlobalStateModification` switch. External SDK integration uses a dedicated, diagnosed interop contract that declares all graph accesses and the concrete external side effect.

## Queue model

Pass kind and queue placement remain separate:

- raster work requires a graphics-capable queue;
- compute work runs on graphics unless explicitly declared async-compute eligible or preferred;
- copy work declares copy commands and a closed allowed/preferred queue set;
- present and sparse/external operations use explicit backend capabilities and synchronization contracts.

Each pass supplies an ordered list of allowed queue classes; the first item is its preference. Raster permits graphics only. Compute defaults to graphics, while an async-eligible compute pass explicitly orders async-compute and graphics according to its preference. Copy helpers explicitly order the copy-capable classes they support. The backend exposes a stable ordered queue topology; within the first available declared class, the lowest stable backend queue index is chosen. Queue topology, capabilities, and indices are deterministic compiler inputs.

Queue placement cannot affect correctness. Selection never uses profiler history, dynamic load, or an implicit "minimize waits" score. The compiler then places fork, wait, signal, ownership-transfer, and join operations from actual dependencies. It does not silently move arbitrary compute work to async based on shader inspection or a profile database.

A required unavailable capability is a feature/capability error decided before pass execution. Executing a compute or copy operation on another declared compatible queue is defined lowering, not an error-swallowing fallback.

Cross-queue lifetime and alias analysis uses the execution partial order, not a single CPU pass index. Two allocations may alias only if the scheduled batch graph proves that all uses of one happen before all uses of the other.

## Barrier model

The graph emits backend-neutral barrier intents containing physical identity/generation, exact range, previous and next resolved access, execution domains, queue ownership, and the reason for the barrier.

Backend lowering produces, as applicable:

- image or buffer state/layout transitions;
- visibility and memory barriers when state is unchanged but ordering is required;
- UAV/atomic ordering barriers;
- queue release/acquire and waits/signals;
- split transition begin/end pairs;
- transient alias acquire/discard barriers;
- present and external handoff transitions.

Ordinary renderer code does not author raw barriers or request `SkipBarrier`. Effect qualifiers and range non-overlap may prove that a memory barrier is unnecessary. Any expert override must be a narrow, provenance-carrying compiler hint checked against the declared accesses; it cannot create an untracked access.

## Transient lifetime and memory

Lifetime analysis runs after pass culling and queue placement. An allocation is eligible for aliasing only when all of these are proven:

1. physical memory requirements are compatible;
2. scheduled lifetimes have a happens-before relation;
3. neither resource has been imported or extracted across the relevant boundary;
4. no external descriptor or host reference outlives the graph use;
5. an alias barrier can establish the new ownership;
6. the new resource's first access does not read undefined old allocation contents.

Alias safety is exact and never heuristic. If any proof is absent, resources do not alias.

Physical heap pages, resource objects, descriptors, and native views may be pooled or cached below the graph using stable physical keys and GPU completion. Those caches do not create a persistent public graph, graph template, or graph resource identity.

### Declared optimization heuristics

Efficient placement among already-safe alias candidates is a lifetime-constrained bin-packing problem, so optimal packing is not practical for a per-invocation compiler. The implemented **deterministic lifetime-aware best-fit decreasing** rule is fully specified as follows:

1. Partition resources by exact backend memory-compatibility class. Backend-required dedicated allocations bypass suballocation.
2. Sort resources by descending aligned size, descending alignment, stable first-use ordinal, then Graph Resource identity.
3. An existing alias slot is a candidate only when the resource is happens-before comparable with every occupant and all physical requirements agree. Its cost is the aligned increase in that slot's maximum capacity. Choose the least increase, breaking ties by stable slot identity; otherwise create a slot.
4. Sort resulting slots by descending capacity, descending alignment, then stable slot identity. Place each into the aligned free gap of an existing heap page that leaves the smallest remainder, breaking ties by page identity then byte offset; otherwise allocate the next page from the backend's deterministic page-size policy.
5. Occupants of one alias slot use the same offset and receive exact alias acquire/discard barriers. Different slots never overlap byte ranges within a page.

Page sizes, heap tiers, alignment, and mandatory-dedicated requirements are backend capability inputs, not tuned thresholds hidden in the graph compiler. The rule changes memory usage only, never alias eligibility or correctness. Its fallback assigns every resource a non-aliasing slot; allocation failure remains a reported resource error, not permission to violate a lifetime proof.

Native render-pass grouping uses the **stable adjacent raster merge** rule defined in the Raster attachments section. It is deliberately greedy because global pass reordering/search would enlarge the semantic and compile-time surface. It may change native pass count and traffic, never logical dependencies; its fallback is no merging.

The transparent cache requires a bounded retention rule because future reuse cannot be known exactly. The implemented **Render-Coordinator-ordered budgeted LRU** rule is:

1. Both retained immutable-plan bytes and entry count have explicit budgets. A successful plan binding receives a monotonically increasing use ordinal from the Render Coordinator; wall-clock time and worker completion order never affect recency.
2. When retention exceeds either budget, remove the least-recently-bound unpinned entry from lookup, breaking equal-ordinal ties by canonical signature. An entry larger than the byte budget may serve its producing invocation but is not retained.
3. A pinned entry is never reclaimed or used as a reason to wait for device-wide idle. If every candidate is pinned, the cache may temporarily exceed its retention budget and trims again when references retire.
4. Removal from lookup and destruction are separate. Active CPU invocation leases keep the pure managed immutable plan alive after eviction; GPU-visible invocation objects are outside the plan cache and retire independently in the RHI.
5. Cache disablement or zero retention is the correctness fallback: recording still receives a conservative or exact optimized plan and cannot observe a semantic difference beyond timing and memory use.

This is a reuse prediction heuristic, not a correctness rule. Other eviction policies may later be substituted only with the same exact-key, pinning, retirement, and fallback contracts.

Before any of these heuristics is enabled by default or materially retuned, the project heuristic gate requires an acknowledged representative-workload report:

- placement: representative workload, peak committed/used memory, fragmentation, allocation compile time, and the non-aliasing comparison;
- raster merge: native pass count, load/store traffic, tile-memory use where measurable, compiler time, GPU time, and the no-merge comparison;
- compilation-cache retention: exact lookup hit rate, conservative-miss execution rate, optimized-plan use rate, required-join rate, retained bytes and entry count, evictions, recompilations, single-flight contention, compilation CPU time saved, publication latency, and the zero-retention comparison.

Hard harness checks cover safety, legality, determinism, and fallback equivalence. Performance results remain warning/baseline evidence until the heuristic report is acknowledged; a hard test must not assume that a heuristic found a globally optimal packing or grouping.

No additional Render Graph heuristic is proposed:

- async-compute eligibility is declared;
- queue selection is deterministic within the declared allowed set;
- culling and hazard construction are exact;
- barrier merging combines only equivalent adjacent ranges;
- dynamic renderer-list emptiness is not used to cull passes;
- internal sparse/dense range representation is an implementation choice validated by benchmarks, not a semantic policy.

## Transparent compilation cache

There is no public `Template`, `Compiled`, `Instance`, `InstancePool`, variant registry, structural graph object, cache key, or cache-control requirement on renderer features. Every invocation records its real graph. Freeze computes a canonical signature and transparently selects an immutable Compiled Graph Plan from the Render Graph runtime's memory cache.

### Cached-plan boundary

A cached plan may contain only invocation-independent compiler output:

- normalized access cells and producer/hazard edges;
- live-pass selection and stable logical order;
- queue-class selection and synchronization templates;
- raster grouping and attachment store decisions;
- transient lifetime intervals, alias slots, and relative placement;
- barrier templates, record units, and immutable diagnostics keyed by stable ordinals.

It contains no pass payload bytes, closure or user-object references, physical resource handles or generations, native descriptors, bindless indices, external fence values, command allocators/buffers, mutable execution counters, or graph-arena addresses. An invocation owns those values and binds them to a plan after selection. A stable executor identity is part of the signature; the current invocation supplies the executor binding and immutable pass data.

The cache is memory-only and partitioned by compiler/backend/device semantic generation. Shader and pipeline binary caches may persist separately, but a Compiled Graph Plan is not a disk ABI.

### Canonical signature

The signature covers every input that can change compiler output:

- compiler schema and optimization-policy version;
- backend API, device semantic generation, capabilities, queue topology, and memory-requirement classes;
- ordered resource/pass topology and stable executor identities;
- resource descriptors, exact views/ranges, effects, content contracts, attachments, load/resolve operations, and Observable Graph Outputs;
- shader/pipeline candidate identities, effect envelopes, and artifact generations;
- ordered allowed queue classes and explicit external-operation requirements;
- imported-slot descriptors, initial/return abstract contracts, content validity, and the equality/physical-alias pattern between slots;
- validation or debug options that alter compilation rather than diagnostics alone.

Invocation payloads, draw/dispatch counts that do not change declarations, clear values, physical import handle values, descriptor slots, device addresses, fence values, timestamps, and thread timing are not signature inputs. Physical imports are validated on every invocation against the cached slot shape and equality/alias pattern.

The structural hash is only an index. A hit requires full equality of the canonical signature; a hash collision is a miss, never permission to reuse a plan.

### Miss, asynchronous compilation, and publication

When an optimized flight is needed, one exact key has at most one single-flight compilation. Concurrent requests join the same completion rather than duplicating work.

On a cold miss, Freeze produces a deterministic conservative plan that preserves full correctness while disabling optional optimization:

- exact-range producer analysis, Observable Graph Output roots, and pass/resource/view culling are the same in conservative and optimized lowering;
- the original relative order of every live logical pass is retained;
- live transient resources do not alias;
- raster passes do not merge;
- attachment stores and barriers are conservative;
- work uses the most conservative declared legal queues and explicit cross-queue synchronization;
- content, import/return, effect, lifetime, and external-side-effect validation remain exact.

That conservative plan executes the current invocation. A worker compiles an optimized plan from the same immutable snapshot only when an enabled policy can change lowering, currently alias placement or raster merging. With both policies disabled, no semantically duplicate optimized flight is launched. If the conservative plan cannot satisfy a hard memory budget or backend capability, the invocation starts or joins the required exact optimized plan and waits before binding; it never executes a plan for a different signature and never drops a newly declared pass.

Worker completion does not mutate a live plan and does not resume through a captured .NET `SynchronizationContext`. The worker enqueues a candidate for the Render Coordinator. The normal plan-selection boundary is after an invocation's Freeze and before its exact cache lookup: the coordinator snapshots completed candidates, orders them by canonical signature, rechecks the full signature and compiler/backend/device/shader generations, and only then installs valid immutable optimized plans. A background candidate arriving after that snapshot waits for the next invocation. If the current invocation has no legally executable conservative plan, its required same-key join creates a second allowed boundary before plan binding: the coordinator consumes and revalidates that result directly, binds it to the waiting invocation, and retains it only if the cache budget permits. An invocation never changes plans after binding, even if another candidate becomes ready while commands are being recorded or submitted.

If semantic generations change while compilation is running, the candidate is discarded. Cache clearing or eviction removes lookup visibility, and the pure managed `CompiledGraph` payload remains alive only until its active CPU invocation leases end. Native resources and command objects are owned by the invocation/RHI and independently retire against exact GPU completions; replacing a cached plan never calls device-wide idle.

Compilation failure is reported with the frozen recording's provenance. A valid conservative plan may remain usable, but the graph does not invent a renderer fallback, skip a semantic pass, or substitute a stale plan. Renderer quality fallback is selected before Freeze as a different explicit recording.

Unity's current public RenderGraph is a useful but narrower precedent: it records every invocation and transparently reuses compiler context by a graph hash, but its public implementation performs lookup, miss compilation, and pass execution synchronously and keeps mutable execution state in the cached context. SomeEngine adopts the immediate/cache compatibility while requiring immutable plan separation, policy-gated single-flight worker compilation, and safe coordinator publication.

UE's `ParallelSetup`, `ParallelCompile`, and `ParallelExecute` builder flags are precedents for internal task parallelism during one builder's lifetime. The documented compile path still joins as part of the current builder's execution; it does not expose a cross-frame compiled-plan future or hot-swap a graph already being recorded. SomeEngine keeps UE's immediate public semantics while making cross-invocation plan reuse and coordinator publication an internal extension.

Stable lower-layer caches for physical allocations, heap pages, native views, descriptors, shaders, and pipelines remain independent. None of these caches create a persistent public graph or authorize the checkpoint's template/instance/variant model.

## External and advanced operations

Advanced rendering capabilities enter the graph only through explicit accesses and passes:

- upload helpers allocate from a Graphics upload service and add an explicit copy pass;
- readback helpers add a copy to an external readback allocation and an Observable Graph Output;
- indirect draws declare argument and count buffer ranges;
- acceleration-structure build/update/compact declares whole-object source and destination AS accesses plus exact geometry, instance, scratch, indirect, and compaction-query buffer ranges;
- sparse mapping and residency remain owned by the residency system but publish explicit queue/external dependencies before consumers;
- a shading-rate image is an explicit raster attachment read;
- query resolve and timestamp collection are explicit encoder/compiler instrumentation with declared output where observable;
- external upscalers, video, and platform compositors import resources with complete state/lifetime contracts.

The graph does not choose quality fallbacks, substitute pipelines, invent sparse-page policy, or resample history. Pipeline readiness and capability branching happen during renderer setup, after which the chosen pass declaration remains closed.

## Parallelism

The end-state threading model has one logical ordering owner and several bounded worker domains:

```text
game/simulation jobs
        |
        v
immutable render snapshot
        |
        v
Render Coordinator (single-writer topology, stable ordinals, Freeze,
                    cache selection/publication)
       / \
      /   \
setup/compiler workers          command-recording workers
(immutable inputs, stable       (exclusive backend recording context per
 deterministic merge)           frame-slot × queue × worker)
      \   /
       \ /
        v
logical submission sequencer / optional dedicated RHI thread
        |
        v
physical graphics, compute, and copy queues
```

`Render Coordinator` is an ownership role, not a promise that work returns to the operating-system thread that happened to request compilation. It may run on a dedicated render thread or be pumped deterministically by an application-owned render loop. Exactly one coordinator per render-execution/device domain serializes topology mutation, cache publication, per-queue submission order, and retirement decisions; independent devices need not share one global lock.

### Setup and Freeze

Game and simulation work produces an immutable render snapshot before graph recording consumes it. The public builder remains ordered and single-writer; workers never append directly to shared resource, pass, or access registries.

A pass may schedule pass-private setup work after receiving a stable ordinal. A structure-affecting result—resource/pass existence, descriptor, view/range, effect, content contract, shader/pipeline candidate, allowed queue, import contract, or observable output—must join before Freeze and be merged by stable ordinal. Payload-only work such as draw packets or immutable constants may overlap graph compilation, but it must complete before its pass records commands and may not change the frozen signature.

### Compilation and publication

Compiler workers read only the frozen canonical snapshot and immutable backend capability/memory-requirement data. Resource-local normalization, pass-parameter traversal, dependency fragments, and backend lowering may be sharded, but the result is merged by resource/pass/range ordinals rather than task-completion order. A backend query that is thread-affine or mutates device state is captured by the coordinator before dispatch or routed through a dedicated backend service; the compiler does not assume that arbitrary `IDevice` calls are worker-safe.

An optimized compile may outlive the invocation that missed the cache. Completion enqueues an immutable candidate for the Render Coordinator; it does not modify the current invocation, invoke a captured `.NET SynchronizationContext`, or publish from the worker. After Freeze and immediately before exact cache lookup, the coordinator snapshots completed candidates, orders them by canonical signature, and rechecks every compiler/backend/device/shader generation. A stale candidate is discarded and a background candidate that arrives after the snapshot waits for the next invocation. The sole exception is a required same-key join because no legal conservative plan exists: before any plan is bound or commands are recorded, the coordinator waits, validates, and binds that exact result itself. An invocation that already selected a conservative or older immutable plan remains pinned to it; only a later lookup can observe a subsequently published plan.

### Command recording and submission

Before parallel command recording begins, resource realization and the invocation's physical-resource, descriptor, pipeline, and pass-payload binding tables are immutable. Each recording worker exclusively owns its backend recording objects for one frame slot and physical queue: Vulkan command pools/command buffers, D3D12 command allocators/lists, descriptor-arena partitions, scratch storage, and equivalent backend state are never shared concurrently.

Workers record only compiler-assigned record units and pass-local bodies. Global transitions, queue ownership transfers, alias acquire/discard operations, and native render-pass boundaries are materialized exactly once by the submission lane or are assigned exclusively to one record unit under a backend-proven inheritance contract. Worker completion order cannot change barrier placement or logical execution order.

One logical submission sequencer, optionally hosted by a dedicated RHI thread, stitches recorded units and submits them in compiled order for each physical queue. Backend APIs may permit calls from several threads, but arbitrary worker submission is not part of the contract. Inter-queue waits/signals express compiled dependencies; independent GPU graphics, compute, and copy queues may then execute concurrently.

An in-flight task pins its frozen snapshot or plan reference, invocation bindings, external-resource leases, and recording context until its CPU use ends; GPU-visible allocations and imported/extracted resource leases retire against the exact submitted completion values. Each owner observes its own lifetime boundary: plan-cache eviction cannot invalidate an active CPU lease, while device loss, cancellation, or shader reload cannot reclaim native state before its RHI retirement contract. Normal operation does not use device-wide idle for either lifetime.

Execute callbacks are parallel-recordable only when they use immutable pass payload and pass-local capabilities. A truly render-thread-affine or externally serialized integration must use a specific diagnosed contract and executes on its declared lane. It is not represented by a casual `Serial` boolean.

CPU setup jobs, CPU graph compilation, CPU command recording, an optional RHI submission thread, and GPU async-compute execution are independent mechanisms. Enabling one neither implies nor silently enables another.

Unity's Graphics Jobs/threading mode is likewise a lower-level native graphics translation choice, not a contract for background RenderGraph compilation or concurrent mutation/execution of its cached compiler context. It does not change the ownership rules above.

### Current implementation boundary

The former `Graph → Template → Compiled → Instance → Frame` checkpoint has been removed. The current product implementation has one immediate, single-writer `GraphBuilder`; opaque per-recording resources, views, and access tokens; immutable Freeze/canonical data; exact buffer-range and texture mip/layer/aspect validation; content-validity checks; specialized color attachments; compiler-owned barriers; transient placed-resource realization; coordinator-owned submission; and exclusive command-list recording contexts. Worker-lane callbacks record compiled record units concurrently, while `PassRecordingLane.Coordinator` is the explicit render-coordinator-affine contract. Handles and completions carry one `DeviceDomain`, and imported-resource readiness uses an immutable, per-queue-normalized `GpuCompletionSet`. Published-but-pending values become GPU queue waits at the first live use on every selected consumer queue. Fence domain/value data is invocation-only; detached background compilation retains only readiness queue shape because that shape can constrain raster-scope grouping.

Shader contracts freeze the complete `ShaderDesc` interface shape rather than a key alone. `ReflectedAccess`, `DeclaredEffect`, `ReflectedOperations`, and `DeclaredOperations` remain four independent canonical facts; binding kind, descriptor count, scalar/sample type, texture dimension, sample count, and storage image format are preserved alongside them. `Atomic` is admitted only for an exact read-write storage contract, while `Append`, `Consume`, raster ordered, and feedback currently fail closed at RG admission. Every descriptor-array element must map to an exact pass-local buffer or texture view access unless it is explicitly externally managed and resolved read-only. The execute-time command facade rejects undeclared physical handles, copy ranges outside the frozen envelope, non-matching descriptor views, and opaque bind groups. `UsesPipeline` closes the pipeline candidate set, while RHI pipeline metadata proves that its actual shader artifact keys and stages belong to the pass's `UsesShader` contracts. Shader bytecode, asset identities, paths, Slang objects, and invocation handles do not enter the frozen compiler input.

Every `RenderGraph` now owns a memory-resident transparent compilation cache. Lookup requires the canonical signature, byte-for-byte canonical data, `DeviceDomain`, device semantic generation, compiler semantic generation, and compiler-policy bits to match. `CompiledGraph` is the cached immutable lowering payload; `CompilationCache` owns lookup, single-flight work, publication, LRU, leases, and retirement. A miss synchronously produces and pins a deterministic conservative plan for the current invocation. One detached optimized compilation is started per exact key only when alias placement or raster merging is enabled and can produce a different lowering; the default zero policy launches no duplicate flight. Worker completion only enqueues an immutable candidate. The Render Coordinator fixes a completed-candidate snapshot at `Begin`/pre-selection boundaries, orders it deterministically, revalidates its environment, and can publish it only for a later lookup. A deterministic optimized-compiler failure is remembered by the current exact resident entry so repeated hits continue using the conservative plan without restarting a failed worker every frame.

Retention defaults to 128 entries and a deterministic estimated 64 MiB payload budget. Setting either limit to zero is the true zero-retention correctness baseline: the producing invocation keeps its lease, but the plan never enters reusable lookup and no pointless optimized worker is launched. An individual plan larger than the positive byte budget is likewise never admitted. Successful retained bindings receive coordinator-owned monotonic access ordinals. Aggregate entry-count or byte-budget overflow evicts the least-recently-bound eligible exact entry; an active invocation CPU lease is the sole correctness pin for the pure managed plan payload, so the cache may carry temporary budget debt without waiting for device idle. Lookup removal and retirement are distinct: an evicted or replaced plan remains alive only through that CPU lease. Native invocation resources retain their independent exact-fence RHI lifetime. Disposal joins outstanding compiler jobs but does not publish unusable terminal candidates or count shutdown cleanup as runtime eviction.

Public statistics are coordinator-owned consistent snapshots and expose hits, misses, conservative compiles, conservative/optimized plan selections, optimized flights, single-flight joins, candidate publication/drop/failure, eviction/retirement, resident and retiring counts/estimated bytes, recorded command lists, and submissions. `TotalCachePayloadBytes` includes resident payload plus any retiring payload still held by active CPU leases. Optional optimized-compilation diagnostics carry the canonical signature, failure stage, frozen pass names, and original exception; a failing diagnostic consumer cannot invalidate the conservative fallback.

The optimized and conservative compiler paths share validation, exact producer analysis, culling, queue selection, hazards, and barrier correctness. The optimized path may additionally apply transient alias placement and stable adjacent raster merging when their `RenderGraphOptions` policies are enabled; both policies default to disabled pending representative game workloads. Ordinary background flights are started or joined only when an enabled transform can change lowering. `ConservativePlanUnavailableException` is the sole path that waits at the coordinator-owned required boundary, revalidates the environment, and binds the exact optimized result to the current invocation. Transparent exact-plan reuse is active and verified: equal immediate recordings reuse the compiled plan while binding fresh imported handles, waits, clear values, callbacks, descriptors, transient allocations, and command contexts on every invocation.

The compiler implements exact pass/resource/view culling, deterministic no-alias placement, optional lifetime-proven alias placement, optional stable adjacent raster grouping, and compiled execution batches/record units. A batch may contain multiple record units; imported boundary transitions, including untouched texture cells needed for a final whole-resource return state, are represented by internal graphics record units instead of being lost or attached to a dead pass. `CompiledGraphContract` validates live masks, dependencies, queue topology, placements, raster scopes, alias acquires, and the abstract barrier state machine before a plan can execute or publish. The compiler deliberately does not implement profile-guided queue selection or add ordering edges merely to improve packing/merging. Cross-queue accesses remain conservatively serialized where the current abstract state domain requires it; true enhanced-barrier release/acquire lowering is not yet exposed. Public import obtains immutable live-resource metadata from the RHI rather than accepting a caller descriptor, revalidates it at Freeze, and rejects overlapping ranges sharing one opaque physical-allocation identity. Persistent/temporal realization, transient export, and imported return contracts all publish only after their exact completion set; a partial submission failure reports already-published completions but does not claim that every resource reached its requested final state.

The D3D12 backend is a real Windows/WARP implementation for heaps, committed and placed buffers/textures, exact buffer and texture copies, explicit buffer/color/depth/stencil clears, scoped upload/readback mapping, portable partial buffer↔texture copies through `GetCopyableFootprints`, fences, deferred destruction, RTV/DSV/SRV/UAV/CBV views, samplers, bind groups and descriptor arrays, graphics and compute root signatures/pipelines, persistent pipeline libraries, typed indirect draw/indexed-draw/dispatch, native query heaps and clock calibration, swapchains, object names/markers, shader-model capability validation, InfoQueue, and DRED diagnostics. Persistent CPU-only descriptors use device-owned typed page pools and return their slots only when the owning native view/sampler retires. Shader-visible descriptor arenas switch heaps when a page fills, rematerialize the active binding state from CPU descriptor truth, rebind it, and retire old heaps with the exact command allocation; the former fixed 4096-resource/256-sampler failure is not the capacity model. Queue-aware state lowering prevents compute lists from using pixel-only shader states and rejects states illegal on copy lists.

Depth/stencil is plane-aware across RHI, Null, Render Graph, and D3D12. D24S8 resources use a typeless native base, typed DSVs, independent depth/stencil SRV/copy formats and subresource indices, and all writable/read-only DSV flag combinations. Depth-only rendering, independent plane load/store/read-only operations, clear/discard, and D32/D24 buffer readback are supported. Portable color MSAA resolve is explicit (`ResolveSource`/`ResolveDestination`, `Average`) and both Null and D3D12 execute it; multisampled texture-to-buffer copies still require that explicit resolve first. Resource/view contracts now carry 1D/2D/3D resource dimension, explicit view dimension including array/MSAA/cube forms, cube compatibility, and an immutable allowed view-format set. CPU-visible placed Upload/Readback buffers are supported with fixed-state usage validation, while CPU-visible placed textures and graph-created CPU-visible transients remain outside the portable surface. D3D12 raster grouping currently reduces compatible logical passes to one `BeginRendering`/`EndRendering` record unit through the existing OM path; native `BeginRenderPass` and suspend/resume remain future capability-gated lowerings. WARP acceptance covers descriptor arrays, dynamic materialization, compute/UAV output, register/space push constants, strict view ranges, 3D odd-height padded copy footprints, explicit resolve, view-shape lowering, CPU-visible placed buffers, real alias acquire execution, command-list pins, fence retirement, CPU descriptor page reuse, name-insensitive requirements caching, and an error-free InfoQueue. The Null backend mirrors the same state, resolve, view, lifetime, and transactional validation rather than serving as a permissive mock.

## Validation and diagnostics

Cheap structural checks remain active in every build; expensive provenance and poison checks may be development-only. Validation covers at least:

- stale or cross-recording texture, buffer, acceleration-structure, Graph View, and Pass Access values;
- access outside the declared view/range or effect envelope;
- actual shader binding/effect outside the frozen candidate set;
- read-before-produce and invalid imported contents;
- contradictory explicit ResourceEffect and Slang capability;
- invalid content combinations and false full-coverage assertions where detectable;
- execute-time use of an undeclared graph resource;
- bindless access to hidden transient/writable resources;
- invalid attachment combinations and load operations;
- external state/lifetime/fence contract violations;
- alias overlap without a happens-before proof;
- backend barrier lowering that fails to cover the abstract intent;
- canonical-signature hash collisions without full signature equality;
- duplicate same-key cold compilations, publication of stale generations, or mutation of an already-bound plan;
- cache-hit, conservative, and exact optimized execution that disagree on validation, observable outputs, or resource contents;
- concurrent reuse of one backend command pool, command allocator/list, descriptor partition, or mutable invocation table;
- recorded-unit completion order changing compiled barrier placement or per-queue submission order.

Diagnostics must answer:

- why a pass was kept or culled;
- which exact producer and range feeds a read;
- why a barrier, queue wait, or ownership transfer exists;
- why two resources did or did not alias;
- which shader effect fact came from user declaration and which came from Slang;
- which declaration caused conservative lifetime or synchronization expansion;
- which external or bindless boundary owns an otherwise untracked resource.

Required debug modes include graph/transition dumps, serial direct execution, serial command recording, single queue, no culling, no aliasing/extended lifetime, transparent-cache disabled/zero-retention, forced cold miss, conservative-only, synchronous exact optimized compilation, resource poison/clobber, and native validation. Cache and compilation modes must compare the same frozen recording and observable results. Debug modes that disable optimization or concurrency must state that they change scheduling and timing.

## Compiler invariants

For one frozen recording, selection, compilation, binding, and execution form this single end-state pipeline:

```text
single-writer recording
    → Freeze: validate closed declarations and build canonical signature
    → exact cache lookup
        ├─ hit: pin matching immutable compiled plan
        └─ miss: exact conservative lowering for this invocation
                 + conditionally start/join one optimized compilation when policy can change lowering
                 + wait only if the conservative plan is not legally executable
    → bind invocation-only payloads, physical resources, descriptors, and waits
    → record assigned command units
    → stitch and submit in compiled per-queue order

optimized compilation:
normalize ranges and resolve shader-effect envelopes
    → build internal producer epochs and hazards
    → identify observable roots and cull dead work
    → produce a deterministic legal pass order
    → assign declared-compatible queues and synchronization
    → compute partial-order lifetimes
    → choose only proven-safe alias candidates and physical placement
    → lower attachment groups and barrier intents
    → emit an immutable plan and diagnostics
    → coordinator generation check and boundary publication
```

The same declarations, shader/pipeline generations, backend queue topology and capabilities, imported-slot descriptors/equality/physical-alias pattern, import initial and return contracts, and deterministic options produce the same conservative and optimized plans. Actual imported handle values and generations are validated during invocation binding, not embedded in the canonical signature; physical addresses, descriptor heap offsets, profiler timestamps, worker completion order, and thread timing are not structural inputs.

## Naming research

The architectural vocabulary was checked against public engine and API sources before adoption:

- `RenderGraph`, builder, resource handle, view, `UseTexture`, `UseBuffer`, and attachment terminology are established by Unity RenderGraph and UE RDG.
- `TextureAccess` and `BufferAccess` have direct UE precedents in `FRDGTextureAccess` and `FRDGBufferAccess`.
- `AttachmentLoadOp` is established by Vulkan, Daxa, and other graphics APIs.
- `TextureId` and `BufferId` remain qualified by the `SomeEngine.RenderGraph` namespace to distinguish them from `SomeEngine.Graphics.TextureHandle` and `BufferHandle`. GitHub search shows `TextureId` is broadly used for logical texture identity, so it must never be exposed without the RenderGraph namespace/context in diagnostics or serialized schemas.
- [`AccelerationStructureId`](https://github.com/search?q=%22AccelerationStructureId%22&type=code) was checked against public GitHub code and remains qualified by `SomeEngine.RenderGraph`; it is the graph-scoped counterpart to the existing physical `SomeEngine.Graphics.AccelerationStructureHandle`, not a device address or descriptor index.
- GitHub searches for [`PriorContents`](https://github.com/search?q=%22PriorContents%22&type=code), [`WriteCoverage`](https://github.com/search?q=%22WriteCoverage%22&type=code), and [`ResourceEffect`](https://github.com/search?q=%22ResourceEffect%22&type=code) found no established conflicting graphics semantics; these names are specific to the orthogonal contracts defined here.
- Public API identifiers ending in `Plan`, `Run`, or `Program` remain forbidden by the repository naming gate. Shader compiler terminology does not authorize a Render Graph wrapper with those suffixes.

## Checkpoint disposition

The current imported checkpoint is not incrementally preserved as an API compatibility target. Its concepts divide as follows.

Keep as algorithmic reference, subject to replacement and proof:

- graph-scoped `TextureId` / `BufferId` identity;
- pass-local `TextureAccess` / `BufferAccess` capability tokens;
- texture subresource and buffer byte-range normalization;
- dependency, barrier, alias, Null validation, and diagnostics tests.

Remove from the public/product model:

- `Graph → Template → Compiled → Instance → Frame` execution;
- public fields/variant domains, user-visible structural signatures, template-owned cached variants, payload leases, and instance pools;
- retained-template history controllers and implicit resize migration;
- profile-guided queue scheduling;
- public backend `ResourceState` and `PipelineStage` arguments on pass access methods;
- public attachment store operation;
- graph resource `Bindless`, `Aliasable`, and implicit export policy flags;
- generic `NeverCull`, `HasSideEffect`, `RequireQueue`, `Serial`, `DisableFusion`, `SkipTracking`, and global-state escape switches;
- execute-time shader validation that discovers the contract after graph compilation;
- graph-owned bindless generation, residency, pipeline readiness, device recovery, and RenderWorld policy.

Restored checkpoint semantics that now live in the immediate architecture rather than the removed retained model:

- `HistoryOffset`/`HistoryCount` behavior is expressed by graph-owned `Persistent`/`Temporal` resources and read-only `History(framesAgo)` identities;
- extraction is expressed by completion-gated transient export and single-transfer `ResourceExport` ownership;
- deterministic JSON/DOT capture is schema-versioned, while executable replay currently recreates portable resources and replays the captured `CopyBuffer` subset with observable output validation;
- `[PassParameters]` and `[ShaderParameters]` source generation freezes access/view/constant/descriptor glue before execution and pairs shader parameters with cooked asset reflection as the only shader-entry/binding truth.

The implementation is a clean replacement against this architecture and the run harness, not a compatibility refactor of the checkpoint.

## Evidence and contrasts

- [UE Render Dependency Graph programming guide](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine): immediate recording, setup/execute separation, pass-parameter traversal, transient allocation, culling, async compute, validation, and diagnostics.
- [UE `ERDGBuilderFlags`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/ERDGBuilderFlags), [`FRDGBuilder::AddSetupTask`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/FRDGBuilder/AddSetupTask), and [`FRDGAsyncTask`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/FRDGAsyncTask): UE exposes parallel setup/compile/execute as builder-internal tasking with explicit join/lifetime rules; these APIs do not expose a cross-frame compiled-graph future or arbitrary-thread hot swap.
- [UE Parallel Rendering Overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/parallel-rendering-overview-for-unreal-engine): distinguishes game, render, render-worker, RHI, and GPU work, supporting separate ownership contracts rather than one undifferentiated “async render” switch.
- [UE `FRDGTexture`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRDGTexture) and [`FRDGSubresourceState`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRDGSubresourceState): stable graph resource identity with internal subresource producer/state tracking rather than public SSA resource handles.
- [UE `FRDGTextureAccess`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRDGTextureAccess) and [`FRDGBufferAccess`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRDGBufferAccess): explicit non-shader access metadata; UE texture range tracking and whole-buffer contrast.
- [UE `SetAllShaderParametersAsBindless`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/SetAllShaderParametersAsBindless): bindless projection still starts from parameter metadata and data.
- [UE `UseExternalAccessMode`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRDGBuilder/UseExternalAccessMode): untracked direct access is limited to registered/converted resources and read-only states.
- [UE `FRenderTargetBinding`](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/RenderCore/FRenderTargetBinding): public attachment binding exposes load action but not public store policy.
- [Slang user-defined attributes](https://shader-slang.org/slang/user-guide/convenience-features#user-defined-attributes-experimental), [`_AttributeTargets`](https://docs.shader-slang.org/en/stable/external/core-module-reference/types/0attributetargets-01a/index.html), [feature maturity](https://docs.shader-slang.org/en/latest/feature_matureness.html), [reflection documentation](https://shader-slang.org/slang/user-guide/reflection.html#determining-whether-parameters-are-used), [compiler overview](https://shader-slang.org/slang/design/overview.html#the-back-end), and [public `slang.h`](https://github.com/shader-slang/slang/blob/master/include/slang.h): declaration-local attributes are reflectable, while public reflection exposes binding/type/usage facts but no supported final linked-IR resource-effect visitor; this supports explicit authored effects instead of target-binary parsers.
- [Unity RenderGraph recording/cache lookup](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.cs#L1265-L1273), [managed compiler lookup](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.Compiler.cs#L11-L33), [native compiler lookup/miss](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Compiler/NativePassCompiler.cs#L177-L204), and [`RenderGraphCompilationCache`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraphCompilationCache.cs#L8-L95): immediate recording and transparent caching coexist, but the surveyed implementation performs miss compilation and pass execution synchronously and caches mutable compiler context; it is not evidence of background compilation plus boundary publication or concurrent pass recording.
- [Unity `RenderingThreadingMode`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.RenderingThreadingMode.html): Graphics Jobs and render-thread selection concern native graphics command translation; they do not establish asynchronous RenderGraph compilation or safe shared compiler-context execution.
- [Daxa TaskGraph tutorial](https://docs.daxa.dev/tutorial/drawing-a-triangle/task-graph/) and [Daxa shader/bindless integration](https://docs.daxa.dev/wiki/shader-integration/): bindless-by-default resource IDs coexist with exact task attachments; permanently read-only resources may remain outside runtime graph synchronization.
- [Dagor daFrameGraph bindless declarations](https://gaijinentertainment.github.io/DagorEngine/api-references/dagor-render/index/daFrameGraph/declaringNodes.html), [daFrameGraph overview](https://gaijinentertainment.github.io/DagorEngine/api-references/dagor-render/index/daFrameGraph.html), and [Dagor material bindless path](https://gaijinentertainment.github.io/DagorEngine/api-references/dagor-dshl/index/shaders.html): exact graph resource/access/stage declarations project to bindless slots, while the material system owns the persistent global table.
- [Snowdrop GDC 2024 D3D12 memory-management slides](https://media.gdcvault.com/gdc2024/Slides/GDC%2Bslide%2Bpresentations/Viau_Gauthier_DX12%2BMemory%2BManagement.pdf) and [Microsoft D3D12 binding model](https://learn.microsoft.com/en-us/windows/win32/direct3d12/binding-model): runtime descriptor indices do not restore CPU knowledge of residency, resource state, lifetime, or graph dependencies.
- [O3DE Atom FrameGraph](https://github.com/o3de/o3de/blob/development/Gems/Atom/RHI/Code/Include/Atom/RHI/FrameGraph.h) and [Falcor RenderPass reflection](https://github.com/NVIDIAGameWorks/Falcor/blob/master/Source/Falcor/RenderGraph/RenderPassReflection.h): additional explicit-attachment/reflection precedents that keep persistent material-resource systems separate from graph intermediates.
- [AMD RPS tutorial](https://gpuopen.com/learn/rps-tutorial/rps-tutorial-part1/): useful evidence for resource-view access attributes and content semantics, but its persistent render-graph/RPSL and temporal-resource model is not the chosen public architecture.
- [Granite Render Graph](https://github.com/Themaister/Granite/blob/master/renderer/render_graph.hpp) and [Frostbite FrameGraph presentation](https://www.gdcvault.com/play/1024045/FrameGraph-Extensible-Rendering-Architecture-in): optimization and lifetime references, not authorities for moving renderer history, residency, or semantic pass injection into the graph core.
- [Vulkan command-pool synchronization rules](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html#commandbuffers-pools) and Microsoft's [D3D12 multithreading sample](https://learn.microsoft.com/en-us/samples/microsoft/directx-graphics-samples/d3d12-multithreading-sample-win32/), [command-list recording guidance](https://learn.microsoft.com/en-us/windows/win32/direct3d12/recording-command-lists-and-bundles), and [queue/list design](https://learn.microsoft.com/en-us/windows/win32/direct3d12/design-philosophy-of-command-queues-and-command-lists): parallel recording requires exclusive per-worker pools/allocators/lists, while deterministic queue submission remains a separate ownership problem.
- [Vulkan acceleration-structure specification](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html#acceleration-structures): native acceleration-structure objects retain backing-memory, build/read, lifetime, and explicit-synchronization obligations, supporting a tracked whole-object graph identity plus exact buffer inputs rather than descriptor-only treatment.

See also [[Render-Graph-Culling-Aliasing-Merging-Report]], [[Render-Boundaries]], [[Product-Boundary]], and [[Harness-Definition]].
