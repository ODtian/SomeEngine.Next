# Vulkan Backend

The Vulkan receiver implements `IGraphicsBackend` directly on Vulkan 1.3. It uses timeline
semaphores, synchronization2, dynamic rendering and Slang-generated SPIR-V. Runtime selection is
`--graphics-backend vulkan`; RenderGraph code and workloads remain backend-neutral.

### Binding ABI

Bounded Slang parameters compile to pipeline-owned descriptor-set layouts. SPIR-V descriptor
decorations are normalized to the RHI register-class ABI before module creation. Active SPIR-V
bindings, rather than module-wide reflection alone, determine each live parameter packet.

Unbounded declarations use device-global descriptor indices. `WriteDescriptor` changes CPU staging;
`PublishDescriptors` builds replacement Vulkan descriptor-set generations and commits all replacements
under one publisher lock. Command recording captures the exact published generations it binds, so a
later publish or table disposal cannot rewrite accepted GPU work.

### Extended paths

- `VK_EXT_transform_feedback` is exposed through stream-output buffers and indexed statistics queries.
  Pipeline creation instruments vertex SPIR-V for full, partial and gap declarations.
- Mesh shaders, fragment shading rate, conservative rasterization, vertex divisors, conditional
  rendering, custom border colors and counted indirect dispatch use their native extensions.
- KHR acceleration structures, ray pipelines, shader binding tables, sparse resources, external Win32
  memory/timelines, calibrated timestamps, residency budgets and Win32 swapchains are native paths.
- Vulkan shader records carry ordinary bytes only. `RayTracing.ShaderRecordResourceBindings` is false;
  opaque per-record values use global bindless indices stored in those bytes.

Sampler feedback has no Vulkan equivalent and is not advertised. Work Graphs are not advertised on a
device without a compatible shader-enqueue extension. Bundles are not advertised because the current
portable bundle description does not carry Vulkan dynamic-rendering inheritance state.

### Performance evidence

The standard CPU boundary is `RenderGraph.BeginFrame` through native command-buffer close and queue
submit return. The fixed Dagor/Enlisted-derived high-watermark case has 73 passes, 200 resources and
more than 400 barriers. Current adjacent reports are:

- `artifacts/graphics-benchmarks/vulkan-graph-cpu-postfeatures-repeat.json`
- `artifacts/graphics-benchmarks/d3d12-graph-cpu-postfeatures.json`

Both satisfy the P95 `< 0.5 ms` gate; their adjacent P95 values are in the same performance tier.
