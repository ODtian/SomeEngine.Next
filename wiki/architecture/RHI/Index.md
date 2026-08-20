# RHI Architecture Index

This vault describes the current SomeEngine RHI contract. It records the public model, ownership,
concurrency, capability and evidence rules that are implemented by the current source tree. Concrete
C# declarations and native API facts remain authoritative when this text and code disagree.

## Mental model

A normal caller needs only these concepts:

```text
Adapter -> Device -> Queue
Resource -> View
Pipeline -> Parameter bindings
CommandContext -> RecordedCommands
Submit -> QueueCompletion
Surface -> Swapchain -> Acquire/Present
```

The product entry point is `IGraphicsBackend`. `DeviceFeatures` is used only while requesting Device
creation. After creation, `TryGetCapability<TCapability>` and `Device.Capabilities` are the facts a
caller may rely on.

## Contract pages

- [[Core-Contract]] — public boundary, shader authority, error and allocation rules.
- [[Lifetime-Concurrency-and-Diagnostics]] — Dispose, parent teardown, physical retention and Device Lost.
- [[Descriptors-and-Bindings]] — fixed descriptor tables, parameter data and static samplers.
- [[Queue-and-Commands]] — recording, barriers, submission, completion and queries.
- [[Pipeline-Cache]] — deterministic cache envelope and asynchronous Pipeline creation.
- [[Presentation]] — swapchain configuration, image sequence and expected statuses.
- [[Advanced-Capabilities]] — sparse, residency, DXR, mesh, VRS, Work Graphs and external interop.
- [[D3D12-Backend]] — exact Direct3D 12 lowering, allocator, DRED and native access.
- [[Validation-and-Evidence]] — optional validation, behavior tests and performance protocol.
- [[Implementation]] — module boundaries and implementation invariants.
- [[Performance-Evidence-2026-08-20]] — current three-receiver, Silk.NET-bound NativeAOT / C++ CPU-frame evidence.
- [[Performance-Evidence-2026-08-18]] — superseded historical C# / C++ / Concrete RHI matrix; it is not current closure evidence.
- [[Performance-Evidence-2026-08-17]] — earlier WARP, adapter and receiver evidence; its Direct/C++ comparison is superseded.

## Authority and scope

Slang/S# is the only shader authority. D3D12 and Validation independently consume the same live S#
reflection and component code. Backend-private native placement may exist, but it is not a second
shader model and is never a Validation authority.

The RHI does not expose a public generic receiver family, caller-selectable retirement policy,
descriptor address-space object, descriptor-version object, normalized shader contract, or public
transaction framework. Backend-internal generations, leases, work lists and preparation storage are
implementation details used to preserve native ownership and failure atomicity.

Render Graph is a consumer of this contract. It may automate scheduling and lifetime decisions, but
it must express the resulting barriers, Queue waits and submissions through the same RHI operations.
