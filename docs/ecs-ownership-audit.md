# SomeEngine ECS ownership / borrow / materialization audit

This audit is derived from the complete current-worktree type graph in
[`ecs-type-dependencies.json`](ecs-type-dependencies.json). The graph contains every project type,
the dependency rank, and categorized `state`, `value-state`, `signature`, `creation`,
`inheritance`, `containment`, and executable-body edges. An edge is evidence, not an ownership
annotation: ownership is inferred from construction, escape, publication, replacement, and cleanup
paths.

## Current graph

- 524 types
- 2,624 unique dependency pairs
- 4,720 categorized edges
- 343 strongly connected components, 16 of them multi-type
- maximum dependency rank 16
- 32 exhaustive single-retained-member wrapper candidates
- 62 wrapper-name candidates (`Wrapper` / `Box` / `Adapter` / `View` / `Handle` /
  `Scope` / `Facade` / `Proxy` / `Access` / `Borrow` / `Lease` / `Token` /
  `Cursor` / `Enumerator`), 91 unique candidates in the union
- 8 descriptor-name candidates (`Descriptor` / `Desc` / `Metadata` / `Info` /
  `Definition` / `Schema` / `Manifest`)
- 51 multi-source retained-reference targets requiring ownership review
- 42 effectively visible retainable-collection boundaries, all machine-classified
- 61 remaining `ToArray` sites (59 invocations plus 2 explicitly named materialization APIs)

The readable rank index, wrapper table, descriptor table, multi-source retained-reference table,
and every `ToArray` source position are in
[`ecs-type-dependencies.md`](ecs-type-dependencies.md). The complete diagram is
[`ecs-type-dependencies.svg`](ecs-type-dependencies.svg).

## Ownership decisions

| State family | Canonical owner | Other retained references | Decision |
| --- | --- | --- | --- |
| Published structure | `WorldStructurePublication` held by `World` | serialization admission, hierarchy admission, structural candidate, and publication scopes | The scopes pin or prepare a root; they do not own a second lifecycle. |
| Archetypes and chunks | `Tables` / `ArchetypeRegistry` | query matches, entity locations, packets, serializers | Consumers borrow identity or pin a published generation. Hot iteration uses `ReadOnlySpan<Chunk>`. |
| Queries | `QueryRegistry` owns `QueryRecord`, `QueryState`, and the generated lifetime pin on that record | generated descriptors retain no `World`, `QueryRecord`, or handle box | A descriptor resolves its immutable definition against the current root's registry. The generated pin is canonical state on the current `QueryRecord` and is cloned only with the registry. |
| Query definition | immutable `QueryDefinition` instance | query record, query state, schedule options, generated descriptor | Shared immutable input, not duplicated mutable ownership. |
| Component/buffer/shared/sparse stores | `WorldStructureRoot` | the root-owned subsystems are cross-wired collaborators | No subsystem wrapper owns another copy; the root owns one instance of each lifecycle obligation. |
| Hierarchy and relation generations | `HierarchyDomainStore<TDomain>` / `RelationTypeState<T>` | immutable published views and detached transaction candidates | A candidate owns its replacement generation until publication; readers pin the published immutable backing. |
| Command storage | `CommandBuffer` / `JobCommandBuffer` | deferred handles, writers, producer adapters | Handles and writers are scoped capabilities into command storage, not owners of a second command lifetime. |
| Serialization state | serialization call/plan only | no mutation-side journal | Serialization plans exist only at explicit serialization boundaries and never participate in ECS query/write ownership. |

The multi-source `state` audit still reports legitimate references such as `World`, `Chunk`,
`Archetype`, and `QueryDefinition`. They remain visible intentionally so a later change cannot call
shared retention “ownership” without reviewing lifecycle behavior.

## Duplicate ownership removed

- Removed `Owners.Queries`; `WorldStructureRoot` now owns the single `QueryRegistry` directly.
- Removed `WorldStructureParts`; the published root itself is the structural owner.
- Removed `QueryHandleBox`; generated descriptors resolve their immutable definition through the
  current registry and retain neither a box nor a registry record.
- Removed the descriptor's `ConditionalWeakTable<World, QueryRecord>` after the graph audit proved
  it pinned a record from an obsolete published root. Generated-query lifetime is now recorded only
  on the current registry-owned `QueryRecord`; the descriptor retains no per-World runtime state.
- Removed `ReadWriteMatches`; query state exposes its canonical match backing as spans.
- Removed the mutation-side serialization journal owner and all journal writes. There is no shadow
  serialized-change representation beside canonical ECS state.
- Removed the delta serialization surface because it had no independent apply owner and depended
  entirely on the deleted hot-path journal.
- Removed pure relation/hierarchy import collection shells and pure value wrappers, including
  `RelationEntitySet`, `RelationAdjacencySlot<T>`, `HierarchyTopologyImport<T>`,
  `BufferValues<T>`, and `SharedComponentValue<T>`.
- Removed `SparseRefRO<T>` and `SparseRefRW<T>`; generated sparse jobs now use direct `in T` /
  `ref T` row borrows.
- Removed `ArchetypeEdge`; `StructuralTransition` is the single transition fact.
- Removed `ColumnMetadata`; `Archetype` owns one `ComponentOperations[]` beside the canonical
  `TableComponentIds`. A second descriptor no longer repeats every component ID.
- `Archetype` shape arrays are now private owner state. Public shape access is a zero-copy
  `ReadOnlySpan<T>` borrow, so a caller cannot mutate component IDs, column operations, enable-mask
  layout, or shared-component layout behind the registry's canonical key.
- `Archetype` also owns its chunk list, transition caches, and shared-chunk buckets privately.
  Callers use scalar owner operations and `ReadOnlySpan<Chunk>`; no mutable list or dictionary
  escapes.
- `SortedValueKey` now always acquires its own stable key backing at construction and exposes only
  `ReadOnlySpan<int>`. It no longer shares an `Archetype` or bundle-plan array whose mutation could
  invalidate the dictionary hash.
- Removed `Bundles._key`, which was a second retained reference to
  `BundleSpawnMap.ComponentIds`. The last-plan fast path now compares directly with the canonical
  plan's read-only span.
- Structural transitions, query-match shapes, relation shards, stable query packets, hierarchy
  propagation captures, and topology packet stages no longer expose their owned arrays. Synchronous
  consumers borrow spans; retained asynchronous consumers carry `ReadOnlyMemory<T>`.
- `DynamicBufferHeader<T>` keeps overflow storage private. Buffer code borrows
  `ReadOnlySpan<T>`/`Span<T>` and uses an opaque backing identity only for alias diagnostics.
- Published hierarchy children keep the immutable array private and cross retained reader or
  serialization boundaries as `ReadOnlyMemory<Entity>`.
- `DetachedTableMap` transfers candidate identity dictionaries exactly once into `EntityStore` and
  performs row validation through owner methods; it exposes neither a dictionary nor its
  enumerator.
- No `ResourceOwnership` type or ownership marker file exists. Ownership remains implicit in the
  actual construction/publication/cleanup graph.

## Wrapper and descriptor verdict

The graph uses two independent scans. The first reports every concrete type that retains exactly
one instance member, including positional-record properties synthesized by the compiler. The
second reports every type whose name ends in `Wrapper`, `Box`,
`Adapter`, `View`, `Handle`, `Scope`, `Facade`, `Proxy`, `Access`, `Borrow`, `Lease`, `Token`,
`Cursor`, or `Enumerator`, so adding a generation, token, flag, or capability field cannot hide a
wrapper from the single-member scan. Their union contains 91 types;
the complete member/method/source tables are generated into
[`ecs-type-dependencies.md`](ecs-type-dependencies.md).

All 32 single-member candidates have the following semantic verdict:

| Verdict | Exhaustive candidates | Why the retained member is not a redundant second representation |
| --- | --- | --- |
| Physical inline layout | `SmallInlineStorage<T>`, `DynamicBufferInline<T>` | The field is the CLR inline-array/component layout itself; removing it changes physical storage rather than eliminating a forwarding object. `DynamicBufferInline<T>` is itself the registered inline-array component, so there is no second `BufferInlineStorage<T>` layer. |
| Typed identity or metadata fact | `DeferredEntity`, `DeferredRelationEdge<T>`, `BufferCapacityAttribute`, `Parent<TDomain>`, `RelationEdge<T>`, `ExternalReferenceKey`, `SerializedFieldAttribute` | The retained value plus the closed generic type or validation rule is the identity/schema fact. These types do not claim the wrapped value's lifecycle. |
| Mutable owner, builder, or publication state | `ComponentIndex<TComponent,TKey>.Builder`, `Clock`, `ExceptionAccumulator`, `HierarchyDomainGeneration`, `QueryDefinitionBuilder`, `RelationDirtyEdgeBucket`, `RelationEntityMap<TValue>`, `SharedStores`, `HierarchyMaintenanceEvidence`, `HierarchyPropagationState` | Each type creates, mutates, freezes, validates, publishes, or synchronizes the retained state and enforces an invariant that the underlying value alone does not express. |
| Cursor or serialization-boundary runtime | `ChunkRowEnumerator`, `DataWriter`, `RelationTopologySerializationRuntime<T>` | The cursor advances query state; the writer performs canonical primitive encoding; the relation runtime validates schema/state and reads/writes topology. None is a property-only forwarding shell. |
| Executable command, driver, job, or scoped capability | `JobProducerPlaybackBatch`, `RecordAccessScope`, `DestroyRelationCommand<T>`, `DestroySubtreeCommand<TDomain>`, `ImmediateSystemDriver`, `JobCommandBuffer.PublicationAdapter`, `JobCommandWriter`, `RelationMaintenanceSystem<T>.MaintenanceJob`, `SystemNode<TSystem,TContext>`, `TopologyPacketFinalizer<TDomain>.ParentFinalizerJob` | These types are executable dispatch/capability objects with completion, rollback, release, scheduling, or interface-erasure obligations. They do not own a second copy of ECS state. |

The 62 name-based candidates are also closed:

| Suffix family | Exhaustive candidates | Verdict |
| --- | --- | --- |
| `View` | `BufferView<T>`, `BundleWriteView`, `HierarchyChildrenView<TDomain>`, `QueryChunkView` | The buffer/bundle/query views are scoped borrow surfaces over canonical storage. The hierarchy view pins one immutable published generation and exposes its backing through `Span`; copying is available only through the explicitly named `ToArray`. |
| `Handle` | `QueryHandle` | Typed query identity plus registry record association; no release path and no second query owner. |
| `Scope` | `CommandBuffer.RecordAccessScope`, `JobCommandProducerScope`, `HookExecutionScope`, `RestrictedWorldApiScope`, `SerializationValidationScope`, `StructuralMutationScope`, `World.ReadSnapshotCallbackScope`, `World.SerializationReadRootScope`, `World.SerializationWriteLifetimeScope`, `World.StructuralCandidateScope`, `World.StructuralTransactionScope`, `WorldJobAdmissionScope` | Each scope carries a concrete admission/release/rollback obligation and closes it in `Dispose`; it is not a collection or state facade. |
| `Adapter` | `HierarchyJobAccess<TDomain>.ParentChunkJobAdapter<TJob>`, `HierarchyJobAccess<TDomain>.ParentReadChunkJobAdapter<TJob>`, `HierarchyPropagationAdapter<TDomain>`, `IGeneratedJobEntityAdapter<TJob>`, `JobCommandBuffer.CompletionAdapter`, `JobCommandBuffer.ParallelProducerAdapter<TProducer>`, `JobCommandBuffer.PublicationAdapter`, `JobCommandBuffer.SerialProducerAdapter<TProducer>`, the four directed/undirected `RelationJobAccess<T>` chunk adapters, and `TopologyPacketFinalizer<TDomain>.TopologyCompletionAdapter` | Concrete adapters are executable Job carriers whose `Execute` closes a scheduling boundary; the static/interface entries define generation/scheduling behavior and retain no wrapped state. |
| `Enumerator` / `Cursor` | `SmallList<T>.Enumerator`, `HierarchyChildrenView<TDomain>.Enumerator`, all query row/chunk/pair enumerators and cursors, `RelationEdgeQuery<T>.Enumerator`, `RelationEntityMap<TValue>.Enumerator`, and both relation slot-table enumerators | Synchronous enumerators are `ref struct` span borrows where possible. Retainable enumerators pin an immutable COW generation through `ReadOnlyMemory<T>` or an immutable storage object; none exposes or accepts a raw borrowed array. |
| `Access` | Query access facts, relation endpoint access, generated Job access facts, storage-specific Job access helpers, hierarchy/relation topology write access, and `WorldStorageAccess` / `WorldJobStorageAccess` / `WorldTopologyAccess` | Value facts identify an admitted capability; executable helpers construct exact resource ranges. They neither forward a single owner object nor own the underlying ECS storage. |
| `Lease` / `Token` | `DurableSaveStore.OperationLease`, `HookCommandToken` | Each carries a concrete release or validated execution obligation; it is not a data wrapper. |

There are no remaining project types ending in `Wrapper`, `Box`, `Facade`, or `Proxy`. In
particular, the deleted `QueryHandleBox` cannot re-enter through a differently counted field shape.

All 8 descriptor-name candidates have a non-wrapper verdict:

| Candidate | Verdict |
| --- | --- |
| `QueryDefinition` | Canonical immutable normalized query model. It owns terms, compiled value accesses, Job-storage admissions, the query key, and write facts. |
| `GeneratedQueryAccessDescriptor` | Owns the normalized generated access set, filter-composition cache, relationship/world-write facts, and parallel-safety validation. The graph records seven retained members and five ordinary methods. It stores no `World`, `QueryRecord`, row, chunk, span, `ref`, or release token. |
| `QueryableTypeInfo` | Four-field registry fact (`Type`, component ID, storage path, capabilities) plus classification logic. |
| `ComponentInfo` | Canonical registry record containing the complete component capability and `ComponentOperations` table; it does not wrap a second metadata object. |
| `ComponentMetadata<T>` | Static closed-generic bridge to the canonical registry and type-erased operations. It has no instance wrapper state. |
| `JobStorageTypeMetadata<T>` | Static recursive alias-safety classification and validation, with no instance wrapper state. |
| `RelationSchema` | Three independent relation invariants plus schema validation. |
| `WorldCheckpointInfo` | Three independent envelope coordinates (`PayloadOffset`, `PayloadLength`, `TotalLength`), not a wrapped checkpoint object. |

`ColumnMetadata` was the sole duplicate descriptor found by this audit: it repeated component ID
beside an operations record. It was deleted rather than decorated with an ownership marker.
The strengthened positional-record scan also found and removed `WorldSerializer.ManifestEntry`,
which did nothing beyond forwarding one `SerializationTypeRuntime`; manifest resolution and payload
application now consume the canonical runtime directly.
Graph generation now rejects any descriptor-name candidate that retains exactly one instance
member, so another property-only descriptor shell cannot silently re-enter the model.

## Zero-copy borrows

- Archetype, table, query-registry, query-state, relation-batch, and topology-runtime reads expose
  `ReadOnlySpan<T>` over owner backings.
- Mutable row access uses direct `ref T`; read-only row access uses `ref readonly T` / `in T`.
- Buffer and bundle inputs use `Span<T>`, `ReadOnlySpan<T>`, or `ReadOnlyMemory<T>` according to
  whether the data must cross an asynchronous ownership boundary.
- `Chunk` keeps every table and jagged array inside the current private `ChunkStorage` owner. It
  lends entity rows, typed component rows, individual add/write-version rows, and mutable masks as
  `Span<T>`. Heterogeneous `ComponentOperations` reads and copies one row through `ref byte`; the
  column array never crosses the `Chunk` owner boundary. A one-row `System.Array` exists only as an
  explicitly owned hook/rollback snapshot.
- `QueryDefinition` lends its normalized Job admission set as `ReadOnlyMemory<T>` so the
  stack-local admission request can retain the borrow without copying; Systems opens `.Span` only
  while compiling resource ranges.
- Relation adjacency shards expose `ReadOnlySpan<T>` for synchronous queries and
  `ReadOnlyMemory<T>` only for the public immutable generation snapshot that must pin its backing.
  Serialization receives a span and never receives the shard array.
- Topology packet stages expose read spans and owner methods that create Job resource identities;
  their entity, parent, and packet-edit arrays remain private to the stage owner.
- `List<T>` remains only as an owner's mutable storage implementation. Hot readers use
  `CollectionsMarshal.AsSpan` behind internal span surfaces.
- Hierarchy terminal-destroy and child-shard construction now accept `ReadOnlySpan<Entity>`;
  relation dirty-edge cleanup accepts `ReadOnlySpan<RelationEdge<T>>`.
- A `List<T>` crossing one of those synchronous boundaries is opened with
  `CollectionsMarshal.AsSpan`; no `IEnumerable<T>` or `IReadOnlyList<T>` borrow is retained.
- `DetachedTableMap` owns validation iteration and transfers its identity maps once; callers receive
  scalar counts/results, not a mutable collection or retained enumerator.
- `HierarchyChildrenView<TDomain>` is an explicit immutable published-generation snapshot, not a
  live collection borrow. Its `ReadOnlyMemory<Entity>` pins the immutable array and its `Span`
  opens a synchronous view without copying; its `ToArray` is an explicitly named caller-owned
  snapshot operation.
- `GeneratedQueryAccessDescriptor` receives its public `params` input as
  `ReadOnlySpan<GeneratedQueryAccess>`, so descriptor construction does not require an array-borrow
  contract.
- `ReadOnlyQueryPacketJobs` carries prepared packet borrows as
  `ReadOnlyMemory<ReadOnlyPacketRange>` through retained Job adapters. Array-pool ownership remains
  solely in `ReadOnlyQueryPacketPlan`.
- Hierarchy propagation materializes candidate/access arrays only at `Schedule`; scheduled Job
  carriers and traversal captures retain those immutable backings through `ReadOnlyMemory<T>`, not
  raw array fields or a second owner.

## Retainable collection boundary audit

The graph rejects every new effectively visible array or retainable-collection field, property,
parameter, or return until it receives an explicit boundary verdict. Borrowed data must instead use
`ref`, `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, or `ReadOnlyMemory<T>`. The current 42 boundaries
are exhaustive and classified as follows:

| Verdict | Sites | Meaning |
| --- | ---: | --- |
| `ownership-transfer` | 28 | The parameter name is `owned...` and the callee becomes the sole mutable owner or publishes an immutable backing. |
| `owner-construction` | 3 | Registry helpers return newly allocated archetype shape/mapping state directly to its owner. |
| `explicit-owner-copy` | 2 | The caller explicitly invoked a public `ToArray`. |
| `stable-snapshot` | 2 | A new stable entity/type snapshot crosses an iteration or validation boundary. |
| `stable-mutation-plan` | 4 | A detached array stabilizes order while canonical state is changed. |
| `one-row-owner-snapshot` | 1 | A hook/rollback path owns a single erased component value. |
| `owner-growth-by-ref` | 1 | `EnsureCapacity(ref T[]?)` replaces an owner's private backing in place. |
| `serialization-destination` | 1 | Serialization fills its own admitted destination set; it does not retain a caller collection. |

There are no effectively visible raw-array fields or properties and no unclassified raw-array
borrows. The graph generation fails on any new unreviewed collection boundary.

## `ToArray` boundary audit

All 61 remaining product/source-generator sites are listed with containing type, member, file, and
line in the generated graph report. They fall into these reviewed boundaries:

| Boundary | Sites | Why materialization is allowed |
| --- | ---: | --- |
| Core owner construction, explicit caller copy, COW publication, rollback snapshot, or stable mutation plan | 37 | The produced array becomes an immutable owner backing, a caller-requested snapshot, or a detached transaction image that must survive source mutation. |
| Systems descriptor construction / job scheduling / stable packet capture | 14 | One array becomes the immutable generated-descriptor backing; the other arrays transfer a detached plan to asynchronous jobs or pin a stable packet proof. |
| Serialization | 3 | Manifest arrays are built only after explicit serialization admission, outside query and mutation paths. |
| Source generation | 7 | Compile-time model/output construction; no runtime ECS path is involved. |

The group counts above close every generated site, rather than sampling representative calls:

| Boundary | Containing operations and exact site counts |
| --- | --- |
| Core owner/COW/transaction/explicit copy, 37 | `Archetype..ctor` (7), `SharedComponentTuple..ctor` (1), `BundleSpawnMap..ctor` (1), `ComponentIndex.Bucket.Publish` (1), `CopyShape.CopyIds` (1), hierarchy `SetOrderPolicy` / `PrepareMaintenance` / `BeginTerminalDestroy` / `RollbackPreimages` / `CollectCandidates` / `StableEntities` (6), unordered/ordered child-shard clone or publication (4), relation endpoint rollback (1), `QueryDefinition` construction/normalization/storage compilation (3), `QueryMatchBuilder.TryCreate` (5), relation order-policy/reorder (2), `StableAffected` (1), mutable adjacency `Freeze` (1), and `StableLiveEdges` (1). The two deliberately named caller-copy APIs are `HierarchyChildrenView<TDomain>.ToArray` and `RelationEdgeQuery<T>.ToArray`. `SortedValueKey` now copies into its owner backing with direct span copy, so it has no `ToArray` invocation. |
| Systems owner construction and async stable capture, 14 | hierarchy propagation `Schedule` (2), `NormalizeRoots` (2), `CaptureTraversal` (3), `BuildDataAccesses` (2); generated descriptor normalization (1); query packet capture (2), packet access capture (1), and topology finalizer access capture (1). |
| Serialization boundary, 3 | admitted world-write plan table runtimes (1), its manifest (1), and manifest validation sorting (1), all after serialization admission. |
| Source generation, 7 | bundle generator (4), job-entity generator (2), serialization generator (1). |

The generator also emits a machine-checked boundary on each site. Current exact boundary counts are
`owner-construction=18`, `cow-clone=2`, `cow-publication=7`,
`explicit-owner-copy=2`, `rollback-snapshot=2`, `stable-mutation-plan=7`,
`async-transfer=13`, `serialization-boundary=3`, and `source-generation=7`.
An unknown file/type/member combination, or an increased count inside an already reviewed member,
makes graph generation fail with `Unreviewed ToArray site` or a review-count error.
The scan covers both `ToArray(...)` invocations and array-returning methods explicitly named
`ToArray`, resolves extension/static/conditional/direct invocation forms semantically, and rejects
an unresolved syntax node named `ToArray`; using `Clone` or `new[]` inside an explicitly named
method cannot evade the boundary gate.

Two invalid sites found by this audit were removed:

- relation endpoint validation no longer materializes a filtered edge array merely to test whether
  any live edge exists;
- bundle descriptor normalization above the stack threshold now uses pooled scratch span storage
  instead of allocating with `ToArray` on the mutation path.

The same semantic graph pass scans every invocation and object creation in both runtime assemblies,
`SomeEngine.ECS` and `SomeEngine.ECS.Systems`, instead of relying on a list of historically named
hot-path files. A call into the `SomeEngine.ECS.Serialization` assembly, `BinaryReader`,
`BinaryWriter`, or `JsonSerializer` makes generation fail. The current hit count is zero: no
unreviewed `ToArray` is present, and no binary encoding, manifest construction, or serialization
journal work is reachable from ordinary ECS query iteration, component modification, buffer write,
sparse write, shared write, relation retarget, bundle write, or scheduled Job access paths.
The reviewed `ToArray` sites remain confined to the explicit owner, snapshot, transaction,
asynchronous-transfer, serialization, and source-generation boundaries listed above.
