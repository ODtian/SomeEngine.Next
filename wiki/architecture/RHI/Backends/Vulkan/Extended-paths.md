# Extended paths 

- `VK_EXT_transform_feedback` is exposed through stream-output buffers and indexed statistics queries.
  Pipeline creation instruments vertex SPIR-V for full, partial and gap declarations.
- Mesh shaders, fragment shading rate, conservative rasterization, vertex divisors, conditional
  rendering, custom border colors and counted indirect dispatch use their native extensions.
- KHR acceleration structures, ray pipelines, shader binding tables, sparse resources, external Win32
  memory/timelines, calibrated timestamps and residency budgets are native paths.
- Win32 Presentation requires VK_KHR_swapchain plus the KHR or EXT swapchain_maintenance1
  feature and its matching surface-maintenance instance extension. Graphics Queue zero must support
  Win32 presentation, and swapchain creation checks that queue against the actual Surface. Devices
  without these prerequisites do not advertise Presentation.
- Each acquisition has an acquire semaphore/fence and a present fence. The render-complete semaphore
  belongs to the native image. Reuse waits for that acquisition/submission and its present fence;
  teardown waits only for the affected swapchain work. Queue completion alone does not prove the
  presentation engine has finished using a semaphore. In-flight capacity is also bounded by the
  native image count minus the surface minimum image count plus one, so an infinite acquisition
  cannot exhaust the images reserved for presentation-engine forward progress.
- Resize and out-of-date recovery rebuild the native swapchain after all acquired/submitted images
  and present fences are released. A minimized surface returns OutOfDate on acquisition and Busy on
  reconfiguration; restoration permits a fresh generation. Unsupported configurations leave the old
  generation usable. Native rebuild failures follow the explicit [reconfigure boundary](../../Presentation/Reconfigure-commit-boundary.md).
- Public swapchain configuration reports the resolved drawable extent. Mailbox/FIFO modes do not
  advertise tearing. Preserved contents and HDR10 metadata are rejected rather than silently ignored.
- Vulkan shader records carry ordinary bytes only. `RayTracing.ShaderRecordResourceBindings` is false;
  opaque per-record values use global bindless indices stored in those bytes.

Sampler feedback has no Vulkan equivalent and is not advertised. Work Graphs are not advertised on a
device without a compatible shader-enqueue extension. Bundles are not advertised because the current
portable bundle description does not carry Vulkan dynamic-rendering inheritance state.

