# ECS Relation and Hierarchy implementation record

This is the lightweight execution record for the accepted breaking cutover. It deliberately uses
neither batch-workstream artifacts nor a compatibility layer. At the time of that cutover, the
known consumer set was repository-internal; this is historical decision context, not a claim about
the repository's current consumers.

## Verified implementation

The cutover was verified by the suites below. Exact pass counts were intentionally removed because
they were a point-in-time snapshot that became misleading as coverage grew. Current reproducible
ECS evidence and fixed workload requirements live in `docs/ecs-certification.md` and CI output.

- [x] `SomeEngine.ECS.Tests`, including the performance regressions.
- [x] `SomeEngine.ECS.Systems.Tests`.
- [x] `SomeEngine.Job.Tests` and `SomeEngine.Job.Dots.Tests`.
- [x] `SomeEngine.ECS.Serialization.Tests` and `SomeEngine.ECS.SourceGen.Tests`.
- [x] `SomeEngine.Core.Tests` and `SomeEngine.Render.Tests`.
- [x] Focused accepted-boundary architecture coverage.
- [x] Focused public API, bundle, command atomicity, relationship ownership, order-tax, clone, and
  rollback gates are part of those suites rather than a manual-only checklist.
- [x] Reproducible build entry point: `dotnet build SomeEngine.slnx --no-restore -v minimal`; retain
  the exact result and third-party warning inventory in the corresponding clean-commit CI evidence.

## Public model and compatibility deletion

- [x] Keep one canonical hierarchy model: `Parent<D>` plus read-only derived `Children<D>`.
- [x] Keep typed hierarchy domains without `HierarchyNode<D>`, Join, Leave, or membership state.
- [x] Delete `ChildBuffer`, `HierarchyLink`, hierarchy-specific delta streams, and duplicate ordered/
  unordered hierarchy engines.
- [x] Delete old `IRelation` / `IExclusiveRelation`, `RelationStore`, `RelationTag`, endpoint-pair
  single-edge operations, and relation delta streams.
- [x] Delete `SystemAccessManifest`, `AccessConflicts`, `SystemSchedule`, and `GlobalDependency`.
- [x] Delete executable `RunQuery` / `RunReadWrite`, raw `World.Get<T>` / `ReadRef<T>`, the internal
  `QueryBuilder` / `QueryView` facade, and the hidden query-construction escape paths.
- [x] Delete `BundleWriter`, `BundleBatch`, `BundleBatchChunk`, `SharedValueSlot`, writer factories,
  old `SpawnBatch`, and old shared-value escape APIs.
- [x] Keep refs, spans, buffers, sparse arrays, query rows, and bundle write views only inside
  runtime-owned callback scopes; reflection/API-shape tests reject the removed surfaces.

## Relationship and hierarchy lifecycle

- [x] Mark canonical relationship sources and derived targets in component metadata and protect both
  roles from generic structural/value APIs, bundles, copy surfaces, command recording, and public
  hook replacement.
- [x] Represent every ordinary relation edge as an independently identified Entity with ordinary
  payload and protected endpoint components.
- [x] Implement directed/undirected topology plus Parallel, UniquePair, UniqueSource, UniqueTarget,
  and OneToOne cardinality.
- [x] Keep endpoint-role order local; ordered and unordered shards can be mixed, while pure unordered
  shards allocate no order key, ordered index, pending placement, or ordered dispatch state.
- [x] Keep `Parent<D>` canonical and `Children<D>` as the immutable last-applied inverse during
  deferred windows. Immediate and deferred maintenance call the same transition kernel.
- [x] Validate liveness, self-parent, cycles, cardinality, final owner-written images, and rollback
  preimages before publication.
- [x] Ordinary parent destruction orphans direct children; only `DestroySubtree` cascades.
- [x] Safely publish immutable per-parent Children and per-endpoint adjacency generations so captured
  views remain stable across later mutations and relation snapshot metadata cannot mix generations.
- [x] Preserve ECS-native Added/Changed/Removed row facts across relocation, including chunk-level
  coarse versions.
- [x] Coalesce repeated remove/re-add/remove churn into one retained `Removed<T>` fact whose value and
  version are refreshed until the consumer clears it.
- [x] Integrate transform pass-through organization nodes and exact keep-world reparent rollback.

## Job, query, and synchronous admission

- [x] Keep system callbacks in stable registration order without a frame-global stage or barrier.
- [x] Close the synchronous lifetime chain across `World`, `SystemGroup`, and `GameWorld`: disposal
  rejects unrelated new roots while allowing already admitted scheduler scopes and descendants to
  finish, system disable/remove waits admitted roots, lifecycle teardown aggregates failures, and
  precondition failures do not leave objects half closed.
- [x] Make hierarchy resources World+domain qualified and relation resources World+payload qualified.
- [x] Turn `JobResourceAccess` declarations into current-scope runtime capabilities, including exact
  identity/generation, mode coverage, whole/range coverage, continuous range unions, and single-work-
  item validation in every safety mode.
- [x] Keep an explicit-dependency submission's resource identity pinned without placing its access
  in the conflict frontier. Dependency success atomically activates the fixed access set against the
  then-current frontier; dependency fault cancels the reservation, and `ReleaseResource` cannot
  recycle a pending identity.
- [x] For large range sets, snapshot each resource's pre-registration frontier so one owner never
  rescans its own packet slices, remove all of an owner's slices per resource in one pass, and reuse
  pooled scratch. Current-scope checks use a per-registration, mode-aware merged interval index so
  contiguous unions and gaps remain exact without rescanning every declaration per work item.
- [x] Make ECS core depend only on Job's lightweight execution-context contract, not `JobSystem` or
  the scheduler. The Systems assembly still owns logical World-storage to Job-resource mapping.
  A raw Job fails closed when that mapper is absent, while a synchronous ECS-only caller does not
  initialize scheduler workers merely to check ambient execution.
- [x] Linearize first typed-access binding with unbound World entry under one gate. If an unbound
  callback wins, binding fails immediately and can be retried after the callback; it never waits
  into callback -> Job completion -> binding deadlock. Once bound, synchronous World calls acquire
  a real caller-thread Resource Owner and the hot path starts with a volatile read.
- [x] Reject compiler-generated async state-machine `Execute` methods at every Core and Dots
  scheduling boundary before state/resource/queue side effects. The generic-static reflection
  result is cached per job/contract pair, so work-item execution pays no reflection or allocation.
- [x] Provide exact table `ComponentJobAccess<T>`, `BufferJobAccess<T>`, `SparseJobAccess<T>`,
  `SharedJobAccess<T>`, hierarchy, relation, and relationship-chunk access wrappers. Shared value
  materialization requires the shared-value capability; membership and precomputed shared-index
  filters remain topology-only.
- [x] Allow ordinary disjoint component queries to overlap. Writable relationship queries retain the
  exclusive owner, final-image validation, and rollback path.
- [x] Serialize same-World topology writers while retaining cross-World parallelism and relaxed
  immutable inverse readers.
- [x] Keep the public hook capability read-only through `DeferredWorld` and reject captured-World
  table/shared/topology writes, including component-local fast paths. Runtime-owned direct
  buffer/sparse borrows may nest under the already-active topology writer; inside a Job they still
  require the exact declared storage capability and one work item. Structural/value reactions use
  the invocation-scoped record-only `DeferredCommandWriter` and become the next command wave; its
  thread/epoch token cannot escape the hook.
- [x] Generate general query/component access sets, `IJobEntity` adapters, stable packet partition
  proof with full captured-tail coverage, and per-chunk/range admission. Canonical multi-work-item
  topology mutation uses its dedicated owner/finalizer proof rather than ordinary query writes.
- [x] Separate clock-window and publication semantics: `AcquireSystemTick()` returns the previous
  baseline while advancing the clock; `AcquireSystemVersion()` returns the newly advanced version
  that a write publishes. Public `ExecuteQuery` no longer accepts a caller-supplied current
  version. Its writable path allocates the current version only after exact query admission, while
  its read-only path does not advance the clock; explicit-current overloads are runtime-internal.
- [x] Capture one logical execution version only after the serial or parallel World-data owner is
  admitted. Generated `IJobEntity` allocates it only when a matching row exists and the immutable
  descriptor contains a direct World write; empty, fully row/chunk-filtered, and matching read-only
  jobs consume no tick. Every table, buffer, and sparse write in that execution publishes the same
  row/coarse/journal version; atomic wrap-aware coarse publication prevents an older packet that
  finishes later from regressing a chunk watermark.
- [x] Make hierarchy propagation even more precise: packet entry and context construction are
  versionless. The first actual `HierarchyPropagationContext.Write` obtains the one shared version
  only after relationship, declared-capability, component-presence, and exact stable-row ownership
  checks pass. All packets reuse it. Read-only callbacks, declared-but-unused write capability,
  empty normalized roots, and rejected accesses consume no tick. The trusted
  `AcquireAdmittedSystemVersion` primitive is internal and does not reopen ordinary World APIs
  inside the restricted callback.
- [x] Keep topology packets as versionless detached staging. `TopologyPacketContext` exposes the
  `LastSystemVersion` selection baseline but no `CurrentSystemVersion`; capture and packet work do
  not acquire a tick, attach a version to the staging arrays, or publish World change facts. Only a
  changed, non-empty staged image schedules the serial finalizer. After that finalizer has acquired
  Parent/topology writer admission, it enters the structural candidate and acquires the one commit
  version used by every Parent/Children row fact, coarse watermark, and journal entry in the
  publication. An empty capture or unchanged image acquires no writer, consumes no tick, and
  publishes no topology revision.
- [x] Attach resource-bearing capture work directly to the semantic dependency for generated jobs,
  topology finalization, and hierarchy propagation. Capture completion owns all packet descendants;
  no detached outer launcher can release the capture lease before its children finish. The topology
  path retains only its resource-free finalizer launcher so an empty packet set never acquires a
  World writer.
- [x] Normalize builder queries and generated filter composition through one implementation; cache
  persistent filter descriptors and per-World handles so warmed descriptor reuse allocates nothing.
- [x] Reclaim explicit query lifetimes through `World.ReleaseQuery`: each `World.Query` acquisition
  retains the interned record, and its matching release decrements that ownership. The final release
  tombstones the record, bumps its generation before slot reuse, and makes stale handles fail closed.
  Exact detached query-registry clones reuse immutable compiled match arrays instead of rebuilding
  them.
- [x] Use World-qualified `PersistentChunkId` plus `StructureEpoch` and `TopologyRevision` for packet
  identity. Checked prefix offsets are derived from the proof rather than copied into a second
  packet model; staging arrays must exactly cover `TotalRowCount`. Evidence includes contiguous
  starts, non-overlap, no A/B/A chunk reuse, total captured chunk rows, tail coverage, total
  rows/chunks, and a fingerprint over all of those facts.
- [x] Make hierarchy propagation validate its declared query access set even for an empty workload.
  Every user callback runs inside a restricted World scope: ordinary World, query, hook,
  CommandBuffer, tick, and other-root hierarchy escapes fault before side effects, while the typed
  `HierarchyPropagationContext<TDomain>` remains the only admitted ECS access path.
- [x] Treat a whole-family writable propagation declaration as authorization only. After traversal
  capture, schedule exactly the coalesced stable row ranges of affected nodes plus external
  ancestor read ranges; context reads/writes revalidate their individual row range. Disconnected
  subtrees of the same component family therefore overlap, while a same-row owner still blocks.
- [x] Reject the ordinary shared `World.Commands()` / `CommandBuffer` surface from every running Job
  on a Job-bound World, including jobs that hold topology Write. Recording, playback, count, clear,
  and dispose all fail before their first state change. Job-side structural production is not
  exposed until it has producer-private segments and a certified stable merge.

## Structural root and CommandBuffer transaction

- [x] Publish one `WorldStructurePublication` containing root identity plus epoch through one release
  write. Readers cannot observe an epoch assembled with another root.
- [x] Build a semantically exact detached World candidate. Entity-location records use fixed 256-row
  persistent pages and table chunks use shared read-only backing with first-write detach; the other
  owners retain exact detached clone implementations. Dynamic-buffer headers retain a per-row
  overflow owner identity: fork/move/promotion may share the immutable backing, and only the first
  real content write to that row detaches it. Allocator frontier, generations, and LIFO free-list
  remain exact.
- [x] Keep persistent entity-record pages identity-only: no page payload references an archetype,
  chunk, or ancestor root. Every live location is resolved through the current root, so retaining an
  old page cannot pin an obsolete structural shell graph.
- [x] Give archetypes/chunks stable persistent identities plus per-candidate ownership/storage
  identities and monotonic storage versions. Untouched record pages and chunk backing are shared;
  touched pages/chunks detach once, old roots remain stable, and abort drops only candidate-owned
  backing. There is no semantic delta log or unbounded ancestor remap chain.
- [x] Bound chunk capacity by a 64 KiB logical payload budget covering entity identities, component
  rows, per-row Added/Written versions, enable masks, and fixed metadata. This deterministic capacity
  contract is not a claim that the CLR object graph or physical allocation is at most 64 KiB.
- [x] Make first-write detach safe for multiple packet writers: chunk backing and entity pages use
  the immutable shared backing itself as a zero-allocation gate, double-check ownership, and publish
  the completed clone once. Concurrent
  same-page/same-chunk ref captures prove that no writer receives an overwritten backing.
- [x] Avoid false first writes: empty prepare/replace/load operations, equal enabled-bit writes, and
  copy operations with no selected table columns do not detach. Read-only DynamicBuffer access does
  not clone overflow storage; ordinary component writes never copy unrelated overflow arrays; one
  row's first real buffer write detaches only that row and later writes in the same generation reuse
  it. Overflow detach preserves capacity but copies only `[0, Count)`, leaving inactive capacity at
  `default`; a managed buffer with overflow keeps its inactive inline storage cleared. Each shared
  bucket owns one canonical immutable shared-component tuple, reused by all of its chunks and by
  detached forks without exposing a mutable array backing. Exact root cloning performs one
  record-location resolver scan rather than duplicate page walks.
- [x] Replay a CommandBuffer FIFO exactly once into that candidate. There is no shadow preflight plus
  second live replay.
- [x] Acquire topology Write before synchronous `World.Flush` removes a non-empty command wave. Any
  running Job is rejected even if it declared topology Write, without consuming or invalidating the
  queued commands; direct playback is rejected before setting its single-playback flag. An empty
  synchronous wave retains the no-admission/no-topology-revision behavior.
- [x] Prepare hook-command overlay capacity before publication, publish root+epoch once, and expose
  stable user-hook commands only as the next wave.
- [x] Replace record-time real entity reservation with `DeferredEntity` and typed deferred edge
  identities. They resolve only after the target epoch is published; failed handles remain invalid.
- [x] CommandBuffer atomicity coverage includes ordinary table, buffer, sparse, shared, index,
  hierarchy, relation, journal, tick, allocator/free-list, old immutable spans, hook fault,
  allocation fault, and a concurrent old-root reader.
- [x] Reject stable hook registration changes during a transaction.
- [x] Bundle Spawn/Add/Replace/SpawnBatch uses the same candidate-root transaction. Batch execution
  reuses one runtime and current chunk but materializes rows lazily, so callbacks and hooks never see
  future default rows.

The required persistent page/chunk COW core is implemented for identity-only entity-location pages,
table chunk backing, and per-row DynamicBuffer overflow storage reachable from those chunks.
Archetype/chunk shells and transition caches remain candidate-private; immutable shared-component
tuples are canonical per bucket and may be shared by all bucket chunks and detached forks;
published Children, adjacency, and index-bucket generations may also be shared safely. Sparse
sets, index buckets, shared stores, mutable relation/hierarchy maintenance state, and the journal
still use exact detached-clone implementations. Converting those additional owners to measured COW
is an optional prepare-cost optimization boundary, not a correctness gap in the page/chunk contract.

## Callback and hook safety boundaries

- [x] `BundleWriteView` is a `ref struct`. Single-row execution validates active token and owner
  thread per operation. Prepared batches validate their one runtime/token/thread at loop entry and
  rely on ref-struct non-escape inside each synchronous callback; descriptor membership, duplicate
  writes, and required-write completion remain checked per row.
- [x] Bundle callbacks cannot directly write table/buffer/sparse/shared/topology storage through a
  captured World. They write declared values through the view or record structural work through
  `World.Commands()`; explicit candidate-tick advancement and journal-suppression control remain
  available and are observed at the actual write.
- [x] A pending bundle row cannot trigger a lazy index backfill for an indexed descriptor that has not
  been written; this prevents a default value from becoming a permanent index bucket entry.
- [x] Bundle tag descriptors emit native `TagAdded` journal events for spawn and add, including a
  tag-only add.
- [x] The journal-suppressed ordinary-component batch path rechecks hook/index/hierarchy/journal
  eligibility on every write, writes the actual current tick and enable mask, and falls back to the
  full side-effect path as soon as eligibility changes.
- [x] Callback/hook faults discard candidate ECS state and hook command overlays.
- [x] Public journal suppression is callback-only (`SuppressSerializationJournal(...)`); no
  disposable suppression token can escape. It rejects every Job callback, acquires topology
  control ownership without a false topology revision, resumes recording before owner release,
  and aggregates body/unwind faults without leaving global suppression active.

The transaction cannot undo ordinary state outside the World. A callback's `ref TState`, a hook's
captured counters/files/other objects, or an Entity copied into external state may already have been
changed before a later fault. Any Entity observed from a transaction that does not publish must be
discarded; it has no post-fault validity guarantee and can numerically alias a later allocation. This
is the explicit boundary of immediate user code, not an ECS partial-publication exception.

## Serialization and integration

- [x] Use only the post-cutover format; no legacy relation/hierarchy importer or dual reader.
- [x] Stream canonical Parent/order and relation edge/endpoint/order state directly from the
  admitted final backing and directly into one new World's final backing.
- [x] Reject dirty, deferred, or in-progress topology before the first caller byte; no projected
  preview generation or topology snapshot/export DTO exists.
- [x] Deserialize only by constructing a new World; existing-World apply/load, structural-candidate
  restore, and load-generated delta-journal paths were removed at the current-only cutover.
- [x] Update entity copy/remap, destruction, Transform, Core, Render, Systems, SourceGen, and tests.

## Remaining work

- [x] Replace entity-location arrays and table chunk backing with measured persistent page/chunk COW
  while preserving the already-tested single root+epoch publication semantics.
- [x] Extend generation COW/detach-on-write ownership to sparse sets, component indices, shared
  stores, and journal pages; structural candidates no longer exact-clone those owners wholesale.
- [ ] Continue profiling relation/hierarchy mutable portions and split or further page any measured
  prepare-cost hotspots. This is a bounded optimization task, not missing transactional correctness.
- [x] Provide Job-side structural production through `JobCommandBuffer`: producer-private segments,
  stable producer-key merge order, producer full-scope lifetime, all-or-nothing producer fault
  handling, and segment-scoped deferred identities are implemented and covered by executable tests.
  The ordinary shared `CommandBuffer` remains intentionally rejected in Jobs; it is not the Job
  writer and must not be presented as a partially safe alternative.
- [x] Add generated general `IJobEntity`/query packet access and a stable per-chunk/range partition
  proof before exposing parallel topology writers.
- [x] Add parallel hierarchy traversal/propagation adapters with the explicit formula
  `packetCount = ceil(disjointRootCount / rootsPerPacket)` and no hidden threshold. The typed
  maintenance token, normalized-root proof, organization-node traversal, ordered/unordered trees,
  and the Core `ParallelTransformPropagation` consumer are executable tests.
- [x] Reject managed/reference-bearing World component aliases, relationship writes, non-table World
  storage, and unproven table ranges before any hierarchy propagation packet starts. Fingerprints
  include entity, canonical parent, and depth rather than only DFS visitation order.
- [x] Run final ECS/dependent-source removed-symbol scans: no old query/ref/bundle/relation/hierarchy/
  schedule symbol remains. Scoped trailing-whitespace scans pass. Repository-wide
  `git diff --check` still reports only two unrelated pre-existing blank lines at EOF in
  `.agents/skills/grill-session-record/SKILL.md` and `.agents/skills/preflight/SKILL.md`, plus
  line-ending conversion notices; those files are outside this implementation.
