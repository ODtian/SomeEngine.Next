# RHI-IMPL-002 — Current source map 

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

