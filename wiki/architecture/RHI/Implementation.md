# RHI Implementation

This note is the executable work boundary for the plan. It uses ordinary repository paths, package
versions, product tests and benchmarks. It does not define a custom planning schema or a second API
project.

### RHI-IMPL-001 — Product modules and dependencies

| Product path | Required end state |
|---|---|
| `src/SomeEngine.Graphics` | Replace the old surface with the backend-neutral RHI. Reference the pinned repository SlangShaderSharp dependency. Do not reference Silk, DXGI, Windows graphics packages or Vortice. |
| `src/SomeEngine.Graphics.Direct3D12` | Replace the backend with one sealed `D3D12Backend` using Silk.NET Direct3D12/DXGI. Own all private native children, mapping, command, descriptor, pipeline, swapchain, query, capability and retirement implementation. |
| `src/SomeEngine.Graphics.Validation` | Add the optional generic Validation Layer described by [[Validation-and-Evidence#RHI-VAL-001 — Pluggable Validation Layer]]. The other two assemblies do not reference it. |
| `src/SomeEngine.Graphics.Null` | Delete the shipped product backend. Deterministic failure injection uses a test-local `IGraphicsBackend`. |

Exact dependency pins for the first D3D12 delivery are .NET SDK 10.0.110, Silk.NET Direct3D12/DXGI
2.23.0, Microsoft Direct3D 12 Agility SDK 1.619.5 (SDK version 619), repository-pinned
SlangShaderSharp and Slang native 2026.4.2. Package restore is locked. Agility and Slang native files
are deployed from their pinned packages/repository dependencies; Vortice, Silk Direct3D Compilers,
DXC reflection and floating `latest` versions are absent.

Silk.NET 2.23.0 is the starting pinned Direct3D 12 package; Agility 1.619.5 supplies the pinned runtime
and debug-layer fixes. Merely running that newer runtime does not make a 1.619-only interface available when
the product has not bound. Every required interface is supplied either by the exact Silk package or
by a committed generated binding extension, and is covered by compilation plus D3D12 integration
tests. Every capability listed in RHI-D3D12-007 is
already delivery scope, so a missing required binding must be resolved in this implementation and
cannot be reported as an unsupported Device. If the binding is extended locally, the generated source
and its native-header/version input are committed and pinned; if Silk is upgraded, this note and the
lockfile move to that exact version together. The dependency replacement commits
`packages.lock.json` for every affected PackageReference project and locked mode must fail on
unresolved dependency drift.
^rhi-impl-001

### RHI-IMPL-002 — Complete replacement scope

The replacement has no compatibility period. Render Graph, renderer/runtime assemblies, samples,
benchmarks and tests are changed in the same repository result to consume the new RHI. Old resource
handles, old barrier APIs, Vortice types, compatibility aliases/facades and obsolete tests are
deleted. Render Graph retains its whole-graph automation and emits explicit base-RHI barriers,
waits, signals and Submit calls.

The implementation writes the real public API directly in product source. Descriptions with
variable-length transient content use `Span`/`ReadOnlySpan` and `ref struct` where lifetime demands
it. Resource, View, Sampler, Pipeline, Binding and domain identities are shallow reference types;
they do not acquire backend generics. `TBackend` travels only through code that executes backend
behavior, with type inference avoiding repeated explicit type arguments.

Every caller-disposable product type implements the single terminal/idempotent contract in
[[Lifetime-Concurrency-and-Diagnostics#RHI-LIFE-001 — One Dispose contract]]. The backend uses one
minimal intrusive parent-child disposal registry for cold parent teardown and separate retained native
payloads for submitted work; it does not add public reference counting, finalizers or per-hot-call
ownership tracking. `SwapchainImage` is sequence-bound but not disposable, because ending its claim
requires an explicit Present or swapchain state transition rather than cleanup syntax.

`DeviceDesc.RetirementType` closes each Device over Manual or Automatic retirement for its complete
lifetime. Device/Context construction selects the corresponding internal recording and retirement
storage once; it does not add a mutable mode field or public-dependency capture work to Manual encode.

The direct D3D12 resource path contains one backend-resource conversion at the actual use site.
Checked/development builds compile a safe cast there; shipping compiles `Unsafe.As<T>` there. The
choice is compile-time and shipping contains no validation-mode load/branch. Non-public base
construction, internal sealed backend identities and immutable owner provenance make the shipping
caller precondition valid. The optional Validation Layer independently checks Device/backend-runtime
provenance and lifetime before forwarding to the same path; it does not create a second backend
method or checked public API.
^rhi-impl-002

### RHI-IMPL-003 — Direct3D 12 mapping coverage

The D3D12 assembly has one explicit implementation for every supported portable value and operation.
No portable enum is converted with an unchecked numeric cast and no unknown value silently falls
through to a native default. Product code and ordinary parameterized tests cover these mapping
families:

- formats, resource/view format families, aspects and planes;
- resource/heap allocation, alignment and linked-node masks;
- canonical creation/import/acquire synchronization facts and their D3D12 initial-state mapping;
- copy, resolve, upload footprints and compressed-block rules;
- Memory/Buffer/Texture/Aliasing barriers, Sync/Access/Layout mapping and cross-Queue handoff;
- rendering attachments and dynamic state;
- stream-output elements, explicit gaps, strides and rasterized stream;
- graphics, compute, mesh, ray-tracing and work-graph pipelines;
- queries, predication, bundles and indirect commands;
- descriptor bindings and Slang-derived root artifacts;
- sparse resources, tile mappings and sampler feedback;
- acceleration structures and ray-tracing shader tables;
- variable-rate shading;
- swapchain format, color space, tearing, latency and presentation;
- external resources/timelines, residency, calibrated timestamps and native access;
- every named capability and operation in [[Advanced-Capabilities]].

Adding a public enum member or operation requires changing the product mapper and its ordinary test.
There is no separate mapping-coverage JSON artifact.
^rhi-impl-003

### RHI-IMPL-004 — Implementation dependencies between subsystems

The code grows along actual dependency edges: backend-neutral identities/descriptions and error rules
support Device/Queue state; those support resources, the parent-child disposal registry, command-slot
pools and retirement; descriptor generations, retained recording-capacity arenas and Slang-derived
binding support pipelines; presentation and optional capabilities use the same Queue/lifetime
foundations; Render Graph and renderers migrate onto the completed public surface. This is one end
state, not a succession of temporary public APIs.

Pipeline-cache byte layout is defined once by the eventual product codec and covered by golden-vector
tests. The Wiki fixes only its observable compatibility and deterministic-merge behavior. Likewise,
private pools, dictionaries, free lists, locks and cache containers are selected while implementing
their owning subsystem; they may not change the observable contracts in the linked notes.
^rhi-impl-004

### RHI-IMPL-005 — Ordinary acceptance work

`SomeEngine.slnx` builds the product without any documentation/reference project. Product unit tests
cover pure state machines and mappings. Direct3D 12 integration tests use WARP and available vendor
hardware. The graphics benchmark project owns the three runners and the protocol in
[[Validation-and-Evidence#RHI-EVID-003 — Fixed performance protocol]]. Wiki links are checked by the
repository's ordinary documentation test for missing note/heading targets and duplicate requirement
block IDs; no RHI-specific script interprets prose as code or claims that keyword presence proves
behavior.

An ordinary compiled product-consumer test owns the receiver-composition proof. Its runtime settings
branch is the only place that names `D3D12Backend` (and future concrete backends). The generic branch
then propagates inferred `TBackend` through create resource → record → End → Submit without erasing it
to `IGraphicsBackend`; the interface branch deliberately stores the same backend implementation as
`IGraphicsBackend` and exercises the same chain through virtual calls. Both use the same non-generic
resource/view/pipeline identities and produce identical backend input and native behavior. This is
real consumer code, not an alias, generated non-generic facade, documentation assembly or method
named by the RHI merely to hide the generic continuation.

Completion means: locked restore and Release build succeed; the old Vortice/handle/Null paths are
gone; real Slang-to-DXIL-to-D3D12 pipeline paths execute; all required state/lifetime/native tests
pass; canonical initial states, mapping windows, recording-capacity growth/failure atomicity,
Memory/Aliasing barriers, all operations in [[Advanced-Capabilities]] and the uniform Dispose/cascade
matrix pass; the shipping direct path contains no Validation Layer, runtime cast selector or checked
cast, while the checked build uses the safe resource cast; stable workloads allocate 0 managed bytes;
and the fixed performance comparisons pass on the required hardware matrix. A missing required
vendor run is reported as an unexecuted release requirement, not counted as success.
^rhi-impl-005
