# RHI Implementation

### RHI-IMPL-001 — Product modules and dependencies

The implementation boundary is:

```text
SomeEngine.Graphics
    public contracts and abstract identities

SomeEngine.Graphics.Direct3D12
    private D3D12 identities, native lowering and diagnostics

SomeEngine.Graphics.Validation
    optional independent contract validation

external/SlangShaderSharp
    one-for-one native Slang bindings used by the RHI
```

Render Graph and Renderer depend on `SomeEngine.Graphics`; they do not define RHI object identity or
shader layout. The benchmark and test projects are evidence consumers, not product API providers.
^rhi-impl-001

### RHI-IMPL-002 — Current source map

Core responsibilities are located by concrete subject:

- `Device`, `Resources`, `Descriptors`, `Pipelines`, `Commands`, `Presentation`, `Synchronization` and
  `Capabilities` in `SomeEngine.Graphics`;
- `Resources.cs` plus `ResourceAllocator.cs` for D3D12 storage;
- `Views.cs`, `ViewObjects.cs`, `Descriptors.cs` and `DescriptorStorage.cs` for native descriptors;
- `RootSignatures.cs`, `Pipelines.cs`, `StateObjects.cs`, `RayTracing.cs`, `WorkGraphs.cs` and
  `PipelineCreation.cs` for Pipeline creation;
- `CommandPreparation.cs` and the concrete `Command*` files for recording;
- `Completion.cs`, `IntrusiveRetirementChain.cs` and `GraphicsObjectRegistry.cs` for lifetime;
- `DeviceLossDiagnostics.cs`, `DebugMessages.cs`, `NativeAccess.cs` and `Presentation.cs` for
  diagnostics/native integration;
- `ValidationLayer.*` and `ValidationParameterBindings.cs` for the independent layer.

Files are split by native responsibility rather than by a generic transaction, artifact or planner
framework.
^rhi-impl-002

### RHI-IMPL-003 — Direct3D 12 mapping coverage

Every public resource usage, view shape, format feature, Queue type, barrier scope/access/layout,
Pipeline state, query type, presentation status and typed capability has an explicit D3D12 mapping or
an explicit unsupported branch.

Native void-return descriptor methods are preceded by complete legality validation. HRESULT-returning
operations preserve the native code and query Device removal where required. Capability discovery is
not treated as proof that a later native creation cannot fail.
^rhi-impl-003

### RHI-IMPL-004 — Subsystem dependency and failure atomicity

The implementation uses a small number of necessary physical mechanisms:

- `DisposeGate` for one logical release;
- parent registries for cascading teardown;
- `NativeLease` for COM and allocation lifetime;
- command capture and Queue retirement for accepted GPU work;
- sparse and presentation generations where immutable native state truly changes;
- concrete preparation helpers before native mutation.

Managed capacity, descriptor ranges, native owner objects and rollback state are acquired before the
commit boundary. Failure tests cover constructor allocation, dependency retain, descriptor
publication, command preparation, Pipeline creation, Queue acceptance and teardown. Test-only fault
injection never appears in the public RHI.
^rhi-impl-004

### RHI-IMPL-005 — Ordinary acceptance work

For an RHI change to be complete:

1. Debug and Release builds pass with the quality analyzer enabled and no baseline suppression.
2. `git diff --check` passes.
3. Graphics, D3D12, benchmark-gate and Render Graph tests pass; destructive groups run in isolation.
4. Affected hardware capabilities execute on available hardware or are recorded `NOT_RUN` with cause.
5. Performance workloads prove equivalent work before timing and distinguish probe, diagnostic and
   certification evidence.
6. Wiki contract pages describe the current API rather than a proposed or deleted model.
7. New source/tests are classified for commit, temporary artifacts are classified separately, and a
   clean recursive checkout can reproduce restore/build/test.

No historical report or uncommitted implementation is sufficient by itself.
^rhi-impl-005
