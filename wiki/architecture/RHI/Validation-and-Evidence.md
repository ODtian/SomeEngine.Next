# RHI Validation and Evidence

### RHI-VAL-001 — Pluggable ValidationLayer

`ValidationLayer` is a sealed non-generic `IGraphicsBackend` wrapper. Construction transfers ownership
of the wrapped backend. The optional message sink is borrowed and callbacks are synchronous.
Disposing the layer closes validation state, reports live objects when configured and disposes the
wrapped backend exactly once.

The layer returns the same public object families and forwards valid operations to the same backend
methods. It does not wrap every resource in another public proxy type and does not create a second
product receiver.
^rhi-val-001

### RHI-VAL-002 — Zero validation overhead when absent

When the application selects `D3D12Backend` directly, no Validation registry, shader diagnostic walk,
message sink or validation dispatch is present. Product code does not contain a runtime branch that
checks whether a layer exists.

The benchmark project measures concrete and interface receiver dispatch separately, but those
measurement adapters do not enter the product assembly.
^rhi-val-002

### RHI-VAL-003 — Rule for every check

Validation checks only facts available from the public API, immutable Device/capability metadata and
live raw S# reflection. It validates ownership, Device/Queue family, range/alignment, resource state,
command lifecycle, descriptor shape, parameter payload, Pipeline export/node identity, split barrier
pairing and submission sequencing.

D3D12 private root slots, descriptor offsets, command shadows and native placement are never
Validation authority. D3D12 and Validation may share pure leaf checks, but not a pre-normalized shader
model that could make the same bug appear correct on both sides.

Creation registration is explicit and failure-atomic: reserve registry capacity, call the backend,
add each state record, and on failure remove additions, Dispose the new object and rethrow. There is
no generic registration transaction framework.
^rhi-val-003

### RHI-VAL-004 — Pipeline creation is authoritative

Validation derives binding/export/node facts before Pipeline creation, then registers the returned
Pipeline only after the backend succeeds. Work Graph materialized entries are read from the created
Pipeline and checked against reflected S# nodes. DXR tables retain the validated Pipeline export
state.

Asynchronous creation performs the same prevalidation, awaits the backend Task, then commits object
and binding state atomically. A faulted Task leaves no registered Pipeline. Task success still means
the native Pipeline is fully usable.
^rhi-val-004

### RHI-VAL-005 — Native validation follows the selected platform

When supported and requested, the Validation Layer enables the backend's D3D12 debug layer, GPU-based
validation, synchronized Queue validation and DRED before Device creation. Native debug messages are
reported with their native severity/category/id and do not replace managed contract checks.

Device removal tests, process-destructive tests and real-window presentation tests execute in isolated
process groups so one terminal native event cannot contaminate unrelated evidence.
^rhi-val-005

### RHI-EVID-001 — Required behavior coverage

The software evidence matrix covers:

- public value equality and XML lifetime/concurrency declarations;
- strict non-D3D12 conformance for resource, descriptor, command, Pipeline, query and presentation;
- WARP resource/view/Pipeline/descriptor/barrier/copy/render/query execution;
- submission preflight, completion retirement and Device/Surface teardown races;
- mapping sequence, allocator reuse and failure atomicity;
- Pipeline cache, asynchronous creation and Slang ownership after caller release;
- static sampler GPU output;
- DXR, sparse, sampler feedback, Work Graph and indirect-command legality/execution where available;
- Device Lost, DRED, quarantine, presentation and process-destructive paths.

A feature without the required hardware is recorded as `NOT_RUN` with the reason. It is never reported
as PASS from metadata inspection alone.
^rhi-evid-001

### RHI-EVID-002 — Workload equivalence

Performance comparisons are accepted only after every receiver produces equivalent work. The
benchmark protocol uses untimed readback hashes, command/submission counts and available native
statistics to prove equivalence before comparing timing.

The current formal receiver set is interface `IGraphicsBackend`, direct C# through the generated
Silk.NET D3D12 COM surface, and native C++ D3D12. There is no Concrete/ImplementationDiagnostic
receiver and no managed manual-vtable D3D12 variant. WARP is functional equivalence only. Hardware
developer probes are exploratory and non-gating. [[Performance-Evidence-2026-08-18]] is superseded;
the current same-source three-receiver results are recorded in [[Performance-Evidence-2026-08-20]].
^rhi-evid-002

### RHI-EVID-003 — Fixed performance protocol

The benchmark schema is `someengine.graphics.performance/v3`. Formal vendor certification fixes:

```text
8,192 warm-up frames
16,384 measured frames
5 interleaved process rounds
10,000 draw/state calls
4,096 barriers
Interface RHI, Direct Silk and Native C++ receivers
```

A three-process 512/1,024-frame fast diagnostic may expose order, frequency or variance bias but can
never certify performance. A one-process 64/256-frame developer probe may select workloads/managed
variants and can never emit a certification PASS.

The protocol records adapter LUID/vendor/device/driver, CPU affinity and priority, power mode,
Agility SDK, validation/DRED state, build hashes, commit/dirty state, execution order, fresh samples,
R-7 percentiles, paired deltas and output equivalence.

High-water command workloads additionally require zero managed allocation, reflection traversal,
string lookup, runtime shader compilation, PSO creation, hidden wait and hidden barrier. Growth frames
are measured separately rather than hidden in warm-up.

The representative CPU-frame schema is
`someengine.graphics.representative-cpu-frame/v1`. It is a separate non-rendering audit that fixes:

```text
1,025 object packets
2,050 draws
40 reusable materials
107 actual material-binding emissions
9 command lists
4 explicit pass barriers
3 persistent recording workers in the parallel variant
2,048 warm-up frames
2,048 measured frames
8 interleaved process rounds; each consecutive 3-round block rotates the 3 treatments
Direct C# through Silk.NET, Native C++, and Interface RHI through Silk.NET
```

Its wall-clock interval begins before object-packet generation and ends after the ninth command list
is closed. It contains no Queue submission, GPU wait or Present. Every implementation consumes the
same source-derived material sequence and must report the same operation counts and workload identity.
This audit is the authority for RHI CPU frame-construction overhead; the older repeated-call loops
remain attribution microbenchmarks only.
^rhi-evid-003
