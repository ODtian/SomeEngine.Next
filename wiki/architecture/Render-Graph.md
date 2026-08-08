# Render Graph

> Status: accepted. The fixed source, dependency-graph, build, and test record is maintained in
> [the RHI / Render Graph concept audit](../../docs/rhi-render-graph-concept-audit.md).

SomeEngine Render Graph is an immediate, invocation-owned dependency graph. Each render invocation
authors the actual resources, views, passes, and observable effects it needs; the same owner is
compiled and executed once. UE RDG is a behavioral reference for immediate authoring, declarative
dependencies, culling, barriers, transient aliasing, and parallel recording, but the implementation
uses the SomeEngine Graphics ownership and synchronization model directly.

## Runtime layers

The rendering runtime has four layers:

1. immutable facts: complete create inputs, capabilities, graph IDs, structural keys, exact
   ranges/regions, `ResourceState`, `QueuePosition`, and `DevicePosition`;
2. lifecycle entities: `Device`, `Queue`, resources, views, layouts, bindings, pipelines, query
   pools, swapchains, work graphs, mappings, acquired images, and the internal transient pool;
3. command scope: one single-use `ICommandRecorder`, explicit retained command variants, and one
   finished `CommandList` owner;
4. graph invocation: one `RenderGraph`, its passes, IDs, canonical rows, active transient claims,
   schedule, and submissions.

Backend implementations are not a fifth layer. Diagnostics is also not a fifth layer: it performs
one explicit projection into a detached immutable snapshot.

## One invocation, one owner

`RenderGraph` is the sole owner of an invocation's authoring storage, parameter bytes,
compiler-derived rows, active placement relations, command schedule, submission bookkeeping, and
cleanup. Its lifecycle advances monotonically:

```text
Authoring -> Closed -> Compiled -> Acquired -> Recorded -> Submitted
```

These labels describe operations inside the same owner. There is no public or internal
`CompiledGraph`, reusable graph template, graph execution object, graph result owner, extraction
owner, or topology cache.

Graph-created resources are transient. They can be consumed by passes or presented through a
declared external presentation operation, but cannot be extracted into persistent owners. Imported
resources remain owned by their callers; the graph borrows and pins them through submission and
tracks exact entry state, readiness, content availability, pass accesses, and return state.

## Authoring and pass ABI

`[PassParameters]` is the only pass-parameter marker. The source generator emits exact row counting,
declaration, and binding code. `AddPass<TPass,TParameters>` reserves all affected columns,
materializes the immutable parameter value, records static callback behavior, and rolls back the
whole append if declaration fails. No open pass builder or mutable pass token escapes.

`GraphId` is the only public graph locator family. It is an opaque invocation-scoped ordinal,
never a physical owner, native handle, descriptor index, synchronization credential, or reusable
resource version.

Each pass declares one actual `QueueType`. Graphics is the default; Compute and Copy are hard
requirements. Unsupported queue or command combinations fail compilation instead of introducing a
preference/domain layer or silently changing queues.

`AccessRow` is the canonical pass-to-resource relation. It contains the graph resource/view
selection, exact range, use, effect, prior-contents contract, and write coverage. Shader argument,
attachment, bindless, query, dependency, placement, and command schedule facts are owner-owned
columns beside that relation, not alternate pass objects.

The pass callback receives a non-escaping `PassCommandScope`. It resolves only declarations that
belong to that pass and exposes graph-safe command methods directly. It does not expose a
`Commands` property, raw `ICommandRecorder`, raw barrier insertion, rendering-scope construction,
`Finish`, or physical owner-returning resolution API.

## Graphics owner and command boundary

The Graphics layer has one portable command-recording owner: `ICommandRecorder`. It is single-use
and single-threaded. `Finish` transfers its closed retained payload and real resource/entity pins
into one sealed `CommandList`. Queue submission consumes that finished owner once; disposing an
unsubmitted list releases that same owner.

Commands and retained payloads use explicit variants:

- barriers distinguish buffer transition, texture transition, buffer unordered-access, texture
  unordered-access, and aliasing;
- descriptor writes distinguish texture, buffer, sampler, and acceleration-structure values;
- acceleration-structure inputs distinguish bottom-level geometry from top-level instances;
- acceleration-structure builds distinguish initial construction from update and include the exact
  scratch `BufferRange`;
- work-graph access distinguishes buffer and texture selections.

The graph compiler preserves the same barrier distinctions in canonical rows. A transition has
either tracked-resource-state or placement-initial-state provenance. Texture variants carry an
exact finite `TextureSubresourceRange`; buffer variants have no texture field. Alias handoffs use a
separate relation and are emitted as aliasing commands without passing through a wide transition
packet.

## Shader interface truth

`ShaderArtifact.Interface.Slots` is the canonical shader-interface input retained by the pipeline
owner. A `ShaderSlot` contains one binding kind, group/binding address, count, stage visibility set,
access effect, optional texture shape facts, and orthogonal `ShaderQualifiers`.

`Atomic`, `Append`, `Consume`, `RasterOrdered`, and `Feedback` remain qualifier membership facts;
they are not an operation packet and are not collapsed into the read/write effect. Unknown bits and
invalid slot shapes fail closed. Pipeline layout linking and graph descriptor validation consume
the same materialized rows, with no second shader contract, reflected-access copy, or linked
interface result object.

The asset schema is converted exactly once at the Render asset projection boundary. RHI and Render
Graph code consume only the Graphics facts.

## Exact resource facts

`Desc` types are complete, scoped inputs for creating one owner. A device or owner factory validates
them, materializes canonical fields once, and does not retain the whole description as owner
metadata or a cache key.

Every stored `Range` is an exact finite half-open interval in one coordinate domain. Every stored
`Region` is an exact multi-axis selection. Whole-resource requests are accepted only at a boundary
where the target owner or authored graph resource is known and are normalized before storage.
`default` never means whole.

Buffers and textures are unique lifecycle owners. Views are typed child owners that pin their
targets and own only validated selection/format/usage facts. `Resource` is borrowed polymorphism
over buffer and texture operations, not another resource owner.

## Transient placement and retirement

The device-owned internal transient pool uniquely owns reusable heaps, placed resources, views,
canonical final states, and retirement coordinates. A graph invocation receives non-copyable
active claims, borrows the underlying owners while the claim is active, and returns every claim on
success or failure. It never owns the caches or exposes a public lease.

Cache equality keys contain normalized structural fields such as allocation location, size, usage,
format, dimensions, and view selection. They do not store whole descriptions or wrap a description
solely to gain equality.

Placement uses one deterministic rule:

1. group by memory type, heap class, and compatibility class;
2. sort by first-use ordinal, then size descending, then resource ordinal;
3. choose the lowest aligned existing interval that fits;
4. reuse only when every prior occupant's terminal command unit happens-before the new first unit;
5. otherwise extend the heap;
6. emit one alias handoff for each occupant change.

The retained output consists only of canonical heap requirements, resource placements, and alias
handoffs. Sorting candidates, interval structures, and reachability indexes remain algorithm-local.

## Compilation, recording, and submission

Graph-owned arena columns provide stable canonical storage. Compilation performs content-aware
backward liveness, exact buffer and texture hazards, RAW/WAR/WAW dependencies, queue validation,
transition/UAV/split barriers, placement, raster-scope formation, command-unit scheduling, task
partitioning, and queue batches. Full discard writes do not retain overwritten producers.

`CommandUnitRow`, `CommandTask`, and `CommandBatch` have different responsibilities:

- a command unit is an indivisible standalone pass, raster scope, alias handoff, or compiler barrier
  operation;
- a command task is one CPU recording partition and may require coordinator-thread execution;
- a command batch is one ordered submission on one actual graphics queue.

CPU affinity and task parallelism do not alter GPU queue dependencies. External readiness and
cross-queue edges are expressed with `QueuePosition` and `DevicePosition`, the same synchronization
facts used by resource retirement and partial-publication failures.

Execution orders compile, resource acquisition, command recording, submission, and claim return.
Pre-submit failure releases unpublished command lists and returns all claims immediately.
Partial-submit failure preserves the published `DevicePosition` and completion-gates cleanup for
the owners already accepted by queues.

## Diagnostics

`SomeEngine.RenderGraph.Diagnostics` materializes the sole durable `RenderGraphSnapshot` only when
explicitly requested. Snapshot storage is detached and immutable. Barrier rows are explicit buffer
transition, texture transition, buffer unordered-access, texture unordered-access, or aliasing
records; placement-initial provenance and exact texture ranges remain visible.

JSON, HTML, DOT, query, and diff operations consume that owner. Snapshot construction and JSON
deserialization validate invariants. Diagnostics does not retain a live graph, wrap the recorder,
record a second command stream, publish mutable arrays, or create a replay/result layer.

## References

- [[Render-Boundaries]]
- [ADR 0006: immediate invocation-owned Render Graph](../../docs/adr/0006-ue-style-immediate-render-graph.md)
- [RHI / Render Graph concept audit](../../docs/rhi-render-graph-concept-audit.md)
- [UE Render Dependency Graph](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine)
