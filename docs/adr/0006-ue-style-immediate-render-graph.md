---
status: accepted
date: 2026-07-26
---

# Keep Render Graph immediate and invocation-owned

## Decision

One `RenderGraph` object is one single-use rendering invocation. It owns authored rows,
compiler-derived rows, active transient-placement relations, command scheduling, submission
bookkeeping, and cleanup. Authoring, close, compilation, recording, submission, and diagnostics
projection are operations on or from that owner; none becomes a second graph, plan, execution,
result, extraction, or reusable-template owner.

Passes declare one actual `QueueType`, exact resource/view accesses, shader arguments, attachments,
queries, and bindless relations through `[PassParameters]` generated code. `GraphId` is the only
public graph locator family. A pass callback receives a non-escaping `PassCommandScope`, which
exposes graph-safe command operations and never returns the raw `ICommandRecorder` or physical
resource owners.

Graph-created resources are transient and cannot be extracted. Imports borrow existing graphics
owners and declare entry state, readiness, content availability, and return state. The graph pins
those owners through submission but never becomes their lifecycle owner.

`ICommandRecorder` is the only portable mutable command-recording owner. `Finish` transfers its
closed payload and real owner pins to one sealed `CommandList`; one queue submission consumes that
finished owner once. Commands use explicit variants. In particular:

- resource barriers distinguish buffer transition, texture transition, buffer unordered-access,
  texture unordered-access, and aliasing facts without inactive fields;
- acceleration-structure inputs distinguish bottom-level geometry from top-level instances;
- acceleration-structure builds distinguish initial construction from update, and always name the
  exact scratch interval;
- descriptor writes and work-graph accesses distinguish their typed resource variants.

The device-owned internal transient pool is the unique owner of reusable heaps, placed resources,
views, canonical final states, and retirement coordinates. The graph holds only active one-time
claims and returns them after successful submission, pre-submit failure, or completion-gated
partial submission. Cache keys materialize normalized structural fields and never retain complete
creation descriptions.

The compiler stores one canonical set of owner-owned rows. Access, dependency, placement, command
unit, and barrier relations are not copied into stage objects. Transition barriers carry either a
tracked before-state provenance or a placement-initial-state provenance; alias handoffs use their
own relation. Buffer and texture barriers remain distinct through diagnostics projection.

Shader artifacts own the canonical shader-interface rows used by pipelines. A slot contains one
kind, one access effect, one stage set, and one orthogonal `ShaderQualifiers` set. Runtime does not
maintain reflected and declared access copies or a second shader-contract object.

Diagnostics is outside the runtime layer model. An explicit request materializes one detached,
immutable `RenderGraphSnapshot`; its barrier rows preserve the actual variants. Diagnostics neither
holds a live graph nor decorates command recording.

## Deterministic transient placement

Transient placement uses one deterministic rule:

1. group resources by memory type, heap class, and compatibility class;
2. order candidates by first-use ordinal, then size descending, then resource ordinal;
3. select the lowest aligned existing interval that fits;
4. reuse an interval only when every previous occupant's terminal command unit happens-before the
   new occupant's first command unit;
5. otherwise extend the heap;
6. emit one alias handoff for every occupant change.

The final graph owner stores only canonical heap requirements, resource placements, and alias
handoffs. Algorithm-local candidates and lookup indexes do not become public or retained domain
objects.

## Consequences

- Backend projects implement the existing graphics owners and command variants; they do not form a
  fifth public layer or publish backend `Info`, `Record`, handle, or dependency mirrors.
- New commands extend the one recorder and retained payload horizontally. New graph features extend
  graph IDs, access variants, or pass declarations horizontally.
- `Desc` values are complete scoped owner-create inputs. Owners materialize canonical facts and do
  not store whole descriptions for later replay.
- Ranges stored beyond a request boundary are exact finite coordinates. Whole/default sentinels and
  bool-tagged inactive fields are invalid.
- `QueuePosition` and `DevicePosition` are the only portable synchronization coordinates used for
  submission, readiness, retirement, waits, partial-publication failures, and snapshots.
- Public lifecycle handles plus `Destroy`, feature device/command-context interfaces, graph
  extraction/result owners, and duplicate alias types are outside this decision.

## Verification record

The fixed implementation and verification record is maintained in
[`rhi-render-graph-concept-audit.md`](../rhi-render-graph-concept-audit.md). That document contains
the complete type audit, survivor dependency graph, closure records, exact source gates, and final
build/test evidence; this ADR does not maintain a parallel verification checklist.
