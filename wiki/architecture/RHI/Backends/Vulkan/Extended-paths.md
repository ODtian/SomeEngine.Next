# Extended paths 

- `VK_EXT_transform_feedback` is exposed through stream-output buffers and indexed statistics queries.
  Pipeline creation instruments vertex SPIR-V for full, partial and gap declarations.
- Mesh shaders, fragment shading rate, conservative rasterization, vertex divisors, conditional
  rendering, custom border colors and counted indirect dispatch use their native extensions.
- KHR acceleration structures, ray pipelines, shader binding tables, sparse resources, external Win32
  memory/timelines, calibrated timestamps, residency budgets and Win32 swapchains are native paths.
- Linux and Windows create Vulkan devices, resources and graphics/compute pipelines through the same
  public backend factory. Linux enables `VK_KHR_xlib_surface`; `SurfaceDesc` carries its X11 window and
  display connection. Windows enables `VK_KHR_win32_surface` and carries its window handle. Both use
  the same acquired-image, graphics-queue submission and swapchain reconfiguration path. Swapchain
  creation checks graphics queue 0 against the actual surface with `vkGetPhysicalDeviceSurfaceSupportKHR`.
- Vulkan shader records carry ordinary bytes only. `RayTracing.ShaderRecordResourceBindings` is false;
  opaque per-record values use global bindless indices stored in those bytes.
- Pipeline creation uses the caller's explicit `PipelineCache`, or one backend-private native cache
  retained by the Device. The private cache reuses driver compilation data across new Shader and
  pipeline objects. Private native creation takes a shared read lock; size checks, replacement and
  destruction take its write lock. Exceeding 64 MiB after successful creation replaces it with an
  empty native cache. Explicit caches retain their existing gate. Pipeline objects retain their
  own Slang data, and asynchronous requests use the existing cache retention and Device worker drain.

Sampler feedback has no Vulkan equivalent and is not advertised. Work Graphs are not advertised on a
device without a compatible shader-enqueue extension. Bundles are not advertised because the current
portable bundle description does not carry Vulkan dynamic-rendering inheritance state.

