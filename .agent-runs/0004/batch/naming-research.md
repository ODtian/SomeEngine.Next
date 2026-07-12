# Run 0004 naming research

Public names introduced while restoring the original Graphics and RenderGraph capability surface
were checked against established GitHub terminology before adoption. This record is evidence for
the naming review target; it is not a claim that SomeEngine copies another project's API design.

## Graphics / RHI terminology

| SomeEngine terminology | GitHub precedent reviewed | Decision |
|---|---|---|
| `CopyTexture`, explicit `ClearBuffer`, `InsertDebugMarker` | [GPUWeb specification source](https://github.com/gpuweb/gpuweb/blob/main/spec/index.bs), [Veldrid command-list surface](https://github.com/veldrid/veldrid/blob/master/src/Veldrid/CommandList.cs) | Reuse direct command verbs already common in backend-neutral GPU APIs. The texture-copy descriptor remains typed instead of exposing D3D12 copy locations. |
| `BufferMapping`, `MapBuffer`, `Unmap`/`Dispose`-scoped access | [wgpu buffer API](https://github.com/gfx-rs/wgpu/blob/trunk/wgpu/src/api/buffer.rs), [GPUWeb specification source](https://github.com/gpuweb/gpuweb/blob/main/spec/index.bs) | Use “mapping” for the ownership/lifetime object and “map” for the operation. SomeEngine deliberately returns a scoped lease rather than an escapable raw mapped span. |
| `WaitIdle`, `QueryPool`, `QueryPoolMetadata` | [Vulkan-Hpp repository](https://github.com/KhronosGroup/Vulkan-Hpp), [Khronos Vulkan samples](https://github.com/KhronosGroup/Vulkan-Samples) | Keep the standard queue/device-idle and query-pool nouns. `Metadata` identifies immutable portable description lookup, not query results. |
| `DrawIndirect`, `DrawIndexedIndirect`, `DispatchIndirect` | [Khronos multi-draw-indirect sample](https://github.com/KhronosGroup/Vulkan-Samples/tree/main/samples/performance/multi_draw_indirect), [Veldrid command-list surface](https://github.com/veldrid/veldrid/blob/master/src/Veldrid/CommandList.cs) | Retain the standard typed command-family names; argument and count buffers stay explicit. |
| `PipelineCacheKey`, `PipelineCacheStats` | [Google Benchmark user guide](https://github.com/google/benchmark/blob/main/docs/user_guide.md), [Vulkan-Hpp repository](https://github.com/KhronosGroup/Vulkan-Hpp) | “Key” and “stats” describe stable identity and observation. They do not imply asynchronous compilation or a hidden heuristic. |

## RenderGraph terminology

| SomeEngine terminology | GitHub precedent reviewed | Decision |
|---|---|---|
| `PassParameters`, `ShaderParameters`, generated parameter binding | [Unreal RDG parameter documentation mirror](https://github.com/staticJPL/Render-Dependency-Graph-Documentation/blob/main/Render%20Dependency%20Graph%20%28RDG%29.md), [Unity Graphics RenderGraph source](https://github.com/Unity-Technologies/Graphics/tree/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph) | Reuse established parameter-block terminology while keeping cooked `ShaderAsset` reflection as SomeEngine's only shader truth. |
| `Capture`, `ReplayExecutor`, executable replay | [Google AGI repository](https://github.com/google/agi), [Unity Graphics RenderGraph source](https://github.com/Unity-Technologies/Graphics/tree/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph) | Separate immutable capture data from the executor that reconstructs and submits it. Structural replay and executable replay remain distinct capability rows. |
| `CaptureValidator`, `GraphCommandBindingValidation`, `ValidateBarrierSequence` | [Unity Graphics `UniversalRenderer` validation/capture usage](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderer.cs), [RenderDoc capture/replay and barrier-validation implementation](https://github.com/baldurk/renderdoc) | Keep validation helpers named for the artifact they validate and use “sequence” for the ordered barrier contract. These are private/internal responsibility extractions, not new public API. |
| `ResourceLifetime`, temporal/history, `ResourceExport` | [Unity Graphics repository](https://github.com/Unity-Technologies/Graphics), [Unreal RDG parameter documentation mirror](https://github.com/staticJPL/Render-Dependency-Graph-Documentation/blob/main/Render%20Dependency%20Graph%20%28RDG%29.md) | Use familiar lifetime/history/export nouns, with transactional frame attempts and completion-gated ownership as explicit SomeEngine semantics. |

## Mechanical result

- No new public type introduced by run 0004 ends in the prohibited `Plan`, `Run`, or `Program` suffixes.
- Original checkpoint terms retained as aliases in the continuity ledger are not treated as new naming decisions.
- Backend-native names (`ExecuteIndirect`, DRED, DXGI, WARP) appear only in backend implementation,
  tests, or evidence; the portable public API does not leak Vortice/D3D12 types.
