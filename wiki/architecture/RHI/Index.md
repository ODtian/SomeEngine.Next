# Render Hardware Interface

> Status: accepted implementation plan. Direct3D 12 is the first delivered backend, not the
> definition of the portable boundary.

This Wiki is the complete pre-implementation RHI plan. It records the public behavior and native
constraints that an implementation cannot infer from a C# signature alone. It does not maintain a
second executable model of code that has not been written yet.

There is no compile-only reference assembly, API generator, JSON contract catalog, trace schema or
special verifier for this plan. During implementation, the real product source defines the exact C#
surface and the ordinary product tests and benchmarks exercise it. The Wiki remains the design and
contract explanation; it is not copied into another documentation format.

## End state

| Product area | Responsibility |
|---|---|
| `SomeEngine.Graphics` | Backend-neutral public objects, descriptions, receiver contract, explicit synchronization, descriptor generations, pipeline-cache semantics and lifetime rules. It references S#/Slang but no Windows graphics package. |
| `SomeEngine.Graphics.Direct3D12` | One sealed `D3D12Backend`, its private native resource types, Silk.NET mapping and command encoding, descriptor heaps, root artifacts, pipelines, swapchain, queries, capabilities and retirement. |
| `SomeEngine.Graphics.Validation` | Optional Validation Layer. Applications construct it only when validation is enabled; the direct backend path has no dependency on it and performs no validation-only work. |
| Render Graph | Global pass/resource/Queue analysis, barrier placement, cross-Queue synchronization, transient lifetime/aliasing and legal graph transformations. |
| Renderers and runtime | Backend-neutral data remains non-generic; only behavior-execution paths propagate `TBackend`. |

Direct3D 12 replaces the old Vortice implementation with Silk.NET. Obsolete handle APIs, the shipped
Null backend and compatibility facades are deleted rather than adapted. Runtime backend switching
quiesces the current graphics runtime and recreates backend resources for the newly selected backend.

The plan fixes observable behavior, ownership boundaries, validation placement and native mapping.
Private file splits, helper names, allocator containers and equivalent cache algorithms are normal
implementation choices; they do not require another architecture decision. Implementers may start
from these notes directly and must not add a parallel planning infrastructure.

## Contract notes

- [[Core-Contract]] — RHI scope, receiver polymorphism, error transport, shader/stream-output authority, naming and allocations
- [[Lifetime-Concurrency-and-Diagnostics]] — ownership, terminal states, retirement, mapping, diagnostics and concurrency
- [[Queue-and-Commands]] — Submit order, completions, command lifetimes, barriers and state suppression
- [[Descriptors-and-Bindings]] — bindless identity, descriptor tables, publication and retained slot objects
- [[Presentation]] — acquired swapchain images, present and resize commit boundary
- [[Advanced-Capabilities]] — sparse resources, residency, ray tracing, mesh/VRS, Work Graphs, indirect work, timestamps, linked adapters and external objects
- [[D3D12-Backend]] — Silk boundary, native access, barriers, root/pipeline mapping and capability availability
- [[Pipeline-Cache]] — cache compatibility, family coverage and deterministic persistence
- [[Validation-and-Evidence]] — optional Validation Layer, complete check placement and implementation acceptance
- [[Implementation]] — repository replacement, dependencies, mapping coverage and ordinary test/benchmark matrix

## Executive view

![[Core-Contract#Boundary summary]]

![[Validation-and-Evidence#Validation boundary]]
