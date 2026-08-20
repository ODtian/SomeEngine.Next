# SomeEngine.Next

SomeEngine.Next is an engine product context. The language below keeps product, rendering, execution, ownership, and verification boundaries explicit.

## Language

**Product Boundary**:
Code, tests, tools, and dependencies declared as part of the repository build, test, and harness gate. Anything inside this boundary must be automatically verifiable.
_Avoid_: temporary boundary, assumed boundary

**Graphics Boundary**:
The portable owner, immutable-fact, command-recording, synchronization, and capability surface implemented by concrete graphics backends. A backend implements this boundary; it does not introduce another public graphics model.
_Avoid_: backend DTO layer, feature-device island, public native handle

**Render Domain**:
Backend-free render concepts such as scene-facing data, material semantics, render-world state, and renderer-independent asset meaning.
_Avoid_: renderer runtime, render backend

**Render Execution**:
A backend-bound renderer implementation that uses accepted RHI and render-graph boundaries to produce frames.
_Avoid_: render domain, migrated render

**Cluster Renderer**:
A render implementation family built around cluster-specific visibility, paging, material planning, and rendering execution.
_Avoid_: base render, engine subsystem

## Render Graph Language

The sole current Render Graph language and architecture is the Wiki contract rooted at
[Render Graph](wiki/architecture/Render-Graph.md). This context file does not repeat its symbols,
suffix rules, resource model, synchronization model, or execution state machines. Read the linked
contract notes and cite their stable `RG-*` identifiers; terminology from historical ADRs, audits,
source code, or prior revisions does not define the current Render Graph.

## Execution and ECS Language

**Resource Owner**:
A scheduled, caller-thread, or external activity whose admitted read/write set is protected until that activity releases its work.
_Avoid_: lease, guard, pending access

**Semantic Dependency**:
An explicit happens-before relationship that selects which completed scope a consumer must observe; it requires predecessor success by default and may explicitly sequence after completion for recovery work.
_Avoid_: resource conflict, submission order, global dependency

**Job Scope**:
The structured lifetime containing one job body and every dynamically attached descendant.
_Avoid_: batch, resource owner, aggregate frame handle

**Lifetime Domain**:
A closeable runtime, World, system, or module boundary that admits root scopes while open and remains alive through all of their descendants.
_Avoid_: lifetime resource, global barrier

**Borrow View**:
An ephemeral `ref struct` view created only while its Resource Owner is active and invalid once that owner ends.
_Avoid_: cached span, storable reference, raw global access

**Structural Transaction**:
An all-or-nothing ECS-owned World mutation boundary for operations that change structural membership or must commit several canonical values as one logical change. The implementation prepares a detached candidate and publishes root identity plus epoch once; it does not roll back ordinary effects in captured state, files, other Worlds, or other objects outside that candidate. It also does not require every explicitly deferred derived view to be brought current by the same operation.
_Avoid_: playback loop, partial structural commit, implicit business callback

**Bundle Write Callback**:
A runtime-owned structural callback whose `BundleWriteView` is byref-like, may write storage only through its declared descriptor, and cannot use a captured World reference as a storage-write bypass. Structural reactions are recorded as the next `World.Commands()` wave; explicit candidate-tick and journal-suppression control are not descriptor writes. An Entity copied out of a transaction that later faults has no post-fault validity guarantee and must be discarded.
_Avoid_: bundle writer object, retained row, nested World mutation

**Immediate Component Hook**:
A synchronous user callback invoked after the current component fact is committed inside the active owner. Its public capability reads through `DeferredWorld` and records structural/value reactions through an invocation-scoped `DeferredCommandWriter` for the next wave. Runtime-owned direct buffer/sparse borrows may reuse the active topology writer; a Job-side non-topology storage borrow must also be declared by the current single-work-item owner. Captured-World table/shared/topology writes and reentrant topology mutation are rejected. Effects on objects outside the World are immediate and are not transactionally reversible.
_Avoid_: relationship invariant hook, deferred system, reentrant topology mutation

**Relation Edge**:
The canonical, independently identified Entity representing one typed source-to-target relation. Single-edge destroy, retarget, and payload access use `RelationEdge<T>` identity; endpoint pairs are only lookup/bulk-selection criteria, while adjacency and presence data are derived.
_Avoid_: endpoint pair as edge identity, relation tag, relation side-store truth

**Relation Cardinality**:
A relation-type-level constraint selecting parallel edges, unique endpoint pairs, unique sources, unique targets, or one-to-one membership. It specializes validation and indexes without storing policy per edge.
_Avoid_: global pair uniqueness, implicit upsert, per-edge cardinality flag

**Relation Direction**:
A relation-type-level choice between directed Source/Target semantics with outgoing/incoming adjacency and undirected EndpointA/EndpointB semantics with one canonical edge and incident adjacency at both endpoints. Undirected endpoint slots remain fixed so payload can associate per-endpoint data, while unordered-pair uniqueness ignores slot order.
_Avoid_: two directed edges for one undirected relation, per-edge direction flag, source/target names on undirected edges

**Relation Adjacency Order**:
The policy local to one endpoint-role adjacency shard. Directed outgoing and incoming memberships, or the two endpoint incident memberships of an undirected edge, may each have independent positions; unordered shards carry no order metadata and make no stable enumeration promise.
_Avoid_: edge-global order key, relation-type-wide order, stable unordered traversal

**Hierarchy Snapshot**:
An optional immutable point-in-time traversal view derived from the currently applied Children state of one Hierarchy Domain. It is neither hierarchy truth nor the only way to read Children.
_Avoid_: hierarchy truth, mandatory hierarchy access path, writable child buffer

**Hierarchy Parent**:
The public canonical `Parent<D>` component identifying an Entity's at-most-one parent within one Hierarchy Domain. Absence means that a workload candidate has no incoming hierarchy edge; Parent does not imply lifetime ownership or cascade destruction.
_Avoid_: owner, membership tag, lifetime parent, cascade relation

**Hierarchy Children**:
The public read-only `Children<D>` RelationshipTarget component or view derived from Parent values. It gives parent-to-direct-child traversal and may be current immediately or represent the last applied Parent state according to the chosen maintenance semantics; callers never mutate it directly.
_Avoid_: hierarchy truth, writable child buffer, membership tag

**Hierarchy Root**:
An Entity selected by a workload's own candidate query that has no `Parent<D>`. Hierarchy core does not define global domain membership, so every Entity without Parent is not automatically a root of every workload.
_Avoid_: all-World root, explicit hierarchy membership, empty Children as membership

**Hierarchy Domain**:
A statically typed, independent Parent/Children relationship namespace with its own forest invariant, local child-order policies, and resource identity. A World has a convenient default domain, while one Entity may have independent Parent relationships in additional domains. The domain itself carries no node-membership state.
_Avoid_: runtime parent channel id, global single tree, hierarchy membership tag

**Native Topology Change Tracking**:
Relation and hierarchy topology changes expressed by ECS-native Added/Changed/Removed history over relation endpoints, Parent, Children, and local-order data, consumed with per-consumer query checkpoints rather than a topology-specific change stream.
_Avoid_: RelationChanges, HierarchyChanges, ParentDelta, topology change stream

**Hierarchy Child-Order Policy**:
The policy local to one parent's immediate child collection: ordered parents preserve explicit sibling order, while unordered parents carry no per-child order metadata and make no stable enumeration promise. The policy does not propagate to ancestors, descendants, or the whole hierarchy graph. An explicit structural transaction may convert an existing collection; promotion supplies a complete permutation or derives a one-time canonical order from stable logical child identities, while demotion discards order metadata.
_Avoid_: ordered hierarchy graph, inherited sibling order, incidental storage order

**Topology Reparent**:
A transform-agnostic hierarchy operation that changes Parent while leaving LocalTransform or any other domain values untouched; for transform-bearing entities this means local space is preserved and world space is recomputed.
_Avoid_: implicit world-position preservation, transform-aware Parent mutation

**World-Preserving Reparent**:
An explicit Transform-layer compound operation that reads fresh effective world transforms, solves a representable new LocalTransform, and publishes Parent plus LocalTransform atomically.
_Avoid_: SetParent boolean default, stale-world reparent, partial Parent/local commit

**Ownership Relation**:
An explicit relation or policy that makes one entity responsible for another entity's lifetime and may request cascade destruction.
_Avoid_: Parent, attachment, hierarchy edge

**Synchronization-Ready ECS**:
An ECS whose logical identities, canonical state, structural journals, and explicit deterministic execution points allow a future synchronization/rollback layer without changing the owner, query, relation, or hierarchy model; it is not a current lockstep guarantee.
_Avoid_: networked ECS, deterministic scheduler timing, implicit lockstep
