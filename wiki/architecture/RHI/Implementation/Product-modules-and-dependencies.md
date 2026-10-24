# RHI-IMPL-001 — Product modules and dependencies 

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

