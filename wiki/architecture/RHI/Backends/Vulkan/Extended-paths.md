# Extended paths 

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

