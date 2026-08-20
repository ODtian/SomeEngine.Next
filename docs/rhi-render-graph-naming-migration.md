# RHI / Render Graph 全类型命名迁移表

> **历史迁移台账，不是命名权威。** 当前 Render Graph 名称只由
> [Render Graph Wiki](../wiki/architecture/Render-Graph.md) 及其引用的 Wiki 命名原则定义。
> 下表中的旧名、迁移目标和 `Row` 名称只用于追溯旧工作树。

此表固定重构前 598 个 RHI、D3D12、Null、Render Graph、diagnostics 与已删除 RG generator 语义类型的逐项结论。当前语义图由 `tools/RhiTypeGraph` 从工作树重新生成。

- 原始类型：598
- 原始决策：保留 181，改名 108，合并 86，删除 223
- 当前终态类型：330（Graphics 171，D3D12 42，Null 30，RenderGraph 68，Diagnostics 19）
- 原始非保留类型残留：0
- 当前以 `Row` / `Kind` / `Use` / `Owner` 结尾的领域类型：0
- 当前完整类型与依赖图：[rhi-render-graph-type-dependencies.md](rhi-render-graph-type-dependencies.md)
- 机器可读迁移表：[rhi-render-graph-naming-migration.tsv](rhi-render-graph-naming-migration.tsv)

## 严格 GPU 命令迁移

命令核验只允许去掉 API 强制家族标记（如 Vulkan `vkCmd`、UE `RHI`）及规范扩展尾缀；不接受同义词推断。CPU helper 不得进入 `CommandList`。

| 原入口 | 终态 | 一手符号 |
|---|---|---|
| <code>ValidateMeshDispatch</code> | 不是 GPU 命令；改为 `CommandList` 的 private 参数校验函数 | 无命令名要求 |
| <code>ReserveBarriers</code> | 删除；它只是 CPU 容量预留 | 无外部 GPU command 对应项 |
| <code>PreResolve(Buffer/Texture/BufferView/TextureView/Sampler/BindGroupLayout/PipelineLayout/Pipeline/QueryPool/BindlessSlot)</code> | 全部删除；resource resolution 是 backend 内部准备，不是 GPU 命令 | 无外部 GPU command 对应项 |
| <code>PreResolveDescriptors / PreResolveGraphDescriptors</code> | 删除；descriptor allocation/update 移到 `Device.UpdateDescriptorSets` | Vulkan `vkUpdateDescriptorSets` |
| <code>PrepareGraphRecording / PrepareRenderingScope</code> | 删除；并入 RG compiler / `BeginRendering` 前置逻辑 | 无外部 GPU command 对应项 |
| <code>SetGraphDescriptors</code> | 删除专用入口；拆为 `UpdateDescriptorSets` + `BindDescriptorSets` | Vulkan `vkUpdateDescriptorSets`, `vkCmdBindDescriptorSets`；Filament `updateDescriptorSet*`, `bindDescriptorSet` |
| <code>BeginGraphBarriers / GraphBufferTransition / GraphTextureTransition / GraphBufferUnorderedAccess / GraphTextureUnorderedAccess / GraphAliasing / EndGraphBarriers</code> | 全部删除专用命令；RG 只构造 `BarrierGroup`，统一调用 `Barrier` | D3D12 `ID3D12GraphicsCommandList7::Barrier` |
| <code>Barriers</code> | 改名为 `Barrier(ReadOnlySpan&lt;BarrierGroup&gt;)` | D3D12 `ID3D12GraphicsCommandList7::Barrier` |
| <code>CopyBuffer</code> | 改名为 `CopyBufferRegion` | D3D12 `CopyBufferRegion`；UE5 `RHICopyBufferRegion` |
| <code>CopyBufferToTexture / CopyTextureToBuffer / CopyTexture</code> | 合为 `CopyTexture` overloads | D3D12 `CopyTextureRegion`；UE5 `RHICopyTexture` |
| <code>ResolveTexture</code> | 改名为 `ResolveImage` | Vulkan `vkCmdResolveImage`, `vkCmdResolveImage2` |
| <code>ClearBuffer</code> | 改名为 `FillBuffer` | Vulkan `vkCmdFillBuffer` |
| <code>ClearUnorderedAccessBuffer</code> | portable surface 合并到 `FillBuffer`；D3D12 专用路径使用 `ClearUnorderedAccessViewUint` | Vulkan `vkCmdFillBuffer`；D3D12 `ClearUnorderedAccessViewUint` |
| <code>ClearTexture</code> | 改名为 `ClearColorImage` | Vulkan `vkCmdClearColorImage` |
| <code>ClearDepthStencilTexture</code> | 改名为 `ClearDepthStencilImage` | Vulkan `vkCmdClearDepthStencilImage` |
| <code>BeginRendering / EndRendering</code> | 原名保留 | Vulkan `vkCmdBeginRendering`, `vkCmdEndRendering` |
| <code>SetPipeline（两个 overload）</code> | 合为 `BindPipeline`；删除 `PipelineBindingPolicy`/`PipelineBindingResult` overload | Vulkan `vkCmdBindPipeline` |
| <code>SetBindGroup</code> | 改名为 `BindDescriptorSets` | Vulkan `vkCmdBindDescriptorSets`；Filament `bindDescriptorSet` |
| <code>SetDescriptors</code> | 移出 `CommandList`，改为 `Device.UpdateDescriptorSets`; recording 时只 bind | Vulkan `vkUpdateDescriptorSets`；Filament `updateDescriptorSetBuffer`, `updateDescriptorSetTexture` |
| <code>SetPushConstants</code> | 改名为 `PushConstants` | Vulkan `vkCmdPushConstants` |
| <code>SetViewport</code> | 原名保留 | Vulkan `vkCmdSetViewport`；Unity `SetViewport` |
| <code>SetScissor</code> | 原名保留 | Vulkan `vkCmdSetScissor` |
| <code>SetStencilReference</code> | 原名保留 | Vulkan `vkCmdSetStencilReference` |
| <code>SetVertexBuffer</code> | 改名为 `BindVertexBuffers` | Vulkan `vkCmdBindVertexBuffers`, `vkCmdBindVertexBuffers2` |
| <code>SetIndexBuffer</code> | 改名为 `BindIndexBuffer` | Vulkan `vkCmdBindIndexBuffer`, `vkCmdBindIndexBuffer2` |
| <code>Draw / DrawIndexed / Dispatch</code> | 原名保留 | Vulkan `vkCmdDraw`, `vkCmdDrawIndexed`, `vkCmdDispatch` |
| <code>DrawIndirect / DrawIndexedIndirect / DispatchIndirect</code> | 原名保留 | Vulkan `vkCmdDrawIndirect`, `vkCmdDrawIndexedIndirect`, `vkCmdDispatchIndirect` |
| <code>DispatchMesh</code> | 原名保留 | D3D12 `ID3D12GraphicsCommandList6::DispatchMesh` |
| <code>DispatchMeshIndirect</code> | 删除专用名；使用 `ExecuteIndirect` | D3D12 `ExecuteIndirect` |
| <code>BuildAccelerationStructure</code> | 改名为 `BuildRaytracingAccelerationStructure` | D3D12 `BuildRaytracingAccelerationStructure` |
| <code>EmitAccelerationStructureCompactedSize</code> | 改名为 `EmitRaytracingAccelerationStructurePostbuildInfo` | D3D12 `EmitRaytracingAccelerationStructurePostbuildInfo` |
| <code>CopyAccelerationStructure</code> | 改名为 `CopyRaytracingAccelerationStructure` | D3D12 `CopyRaytracingAccelerationStructure` |
| <code>SetRayTracingBindGroup</code> | 合并到通用 `BindDescriptorSets` | Vulkan `vkCmdBindDescriptorSets` |
| <code>SetRayTracingBindings</code> | 移出 `CommandList`，合并到 `Device.UpdateDescriptorSets` | Vulkan `vkUpdateDescriptorSets` |
| <code>SetRayTracingPushConstants</code> | 合并到通用 `PushConstants` | Vulkan `vkCmdPushConstants` |
| <code>DispatchRays</code> | 原名保留 | D3D12 `DispatchRays`；Unity `DispatchRays` |
| <code>SetShadingRate / SetShadingRateImage</code> | 改名为 `RSSetShadingRate` / `RSSetShadingRateImage` | D3D12 `ID3D12GraphicsCommandList5::RSSetShadingRate`, `RSSetShadingRateImage` |
| <code>ClearSamplerFeedback</code> | 从 portable surface 删除；D3D12 extension 使用 `ClearUnorderedAccessViewUint` | D3D12 `ClearUnorderedAccessViewUint` |
| <code>DecodeSamplerFeedback / EncodeSamplerFeedback</code> | 从 portable surface 删除；D3D12 extension 使用 `ResolveSubresourceRegion` + 标准 resolve mode | D3D12 `ResolveSubresourceRegion` |
| <code>UseBindlessSlot</code> | 删除；descriptor handle resolution 是 CPU 操作，不是 GPU 命令 | 无外部 GPU command 对应项 |
| <code>DispatchWorkGraph</code> | 改名为 `DispatchGraph` | D3D12 `ID3D12GraphicsCommandList10::DispatchGraph` |
| <code>ResetQueryPool / BeginQuery / EndQuery / WriteTimestamp</code> | 原名保留 | Vulkan `vkCmdResetQueryPool`, `vkCmdBeginQuery`, `vkCmdEndQuery`, `vkCmdWriteTimestamp` |
| <code>ResolveQueryPool</code> | 改名为 `ResolveQueryData` | D3D12 `ResolveQueryData` |
| <code>PushDebugGroup / PopDebugGroup / InsertDebugMarker</code> | 改名为 `BeginEvent` / `EndEvent` / `SetMarker` | D3D12 `BeginEvent`, `EndEvent`, `SetMarker` |
| <code>Finish</code> | 改名为 `Close` | D3D12 `ID3D12GraphicsCommandList::Close` |

### 外部命名证据基线

上表的“出现”只按外部原始 symbol 的 operation token 计算；仅允许剥离 API 强制前缀（例如 Vulkan `vkCmd`、UE `RHI`）和标准扩展尾缀，不接受同义词推断。核验使用以下一手资料：

- UE5：[`IRHICommandContext`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RHI/IRHICommandContext)、[`FRHICommandList`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RHI/FRHICommandList)、[`ERHIResourceType`](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RHI/ERHIResourceType)。
- Unity Graphics（固定 commit `a7e4c051d256a781ab362c64316b125a1e104694`）：[`RenderGraph.cs`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.cs)、[`IRenderGraphBuilder.cs`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/IRenderGraphBuilder.cs)、[`PassesData.cs`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Compiler/PassesData.cs)、[`ResourcesData.cs`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Compiler/ResourcesData.cs)、[`BufferHandle`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraphResourceBuffer.cs)、[`TextureHandle`](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraphResourceTexture.cs)。
- AMD RPS（固定 commit `f3330f5306d15af8529a310f6255225c864b0961`）：[`rps_render_graph.hpp`](https://github.com/GPUOpen-LibrariesAndSDKs/RenderPipelineShaders/blob/f3330f5306d15af8529a310f6255225c864b0961/src/runtime/common/rps_render_graph.hpp)。
- Filament（固定 commit `50aa36845c89201af3e4ef45b72ca2d09d61be60`）：[`DriverEnums.h`](https://github.com/google/filament/blob/50aa36845c89201af3e4ef45b72ca2d09d61be60/filament/backend/include/backend/DriverEnums.h)、[`CommandStream.h`](https://github.com/google/filament/blob/50aa36845c89201af3e4ef45b72ca2d09d61be60/filament/backend/include/private/backend/CommandStream.h)、[`DriverAPI.inc`](https://github.com/google/filament/blob/50aa36845c89201af3e4ef45b72ca2d09d61be60/filament/backend/include/private/backend/DriverAPI.inc)。
- DirectX-Headers（固定 commit `2c305c16da8a4450db8d7f1e7d8d014c8bc665ee`）：[`d3d12.h`](https://github.com/microsoft/DirectX-Headers/blob/2c305c16da8a4450db8d7f1e7d8d014c8bc665ee/include/directx/d3d12.h)；Microsoft Learn：[`D3D12_RESOURCE_BARRIER_TYPE`](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_resource_barrier_type)、[`D3D12_BARRIER_TYPE`](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_barrier_type)。
- Vulkan-Headers（固定 commit `d314eae73fdc90847bb8304a86ee7c6a8ee023b6`）：[`vulkan_core.h`](https://github.com/KhronosGroup/Vulkan-Headers/blob/d314eae73fdc90847bb8304a86ee7c6a8ee023b6/include/vulkan/vulkan_core.h)；[`Vulkan Specification`](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html)。

## 598 个原始类型的完整决策

| # | 程序集 | 原类型 | 决策 | 终态 | 原因 |
|---:|---|---|---|---|---|
| 1 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureBuild</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 2 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureBuildCapabilities</code> | 改名 | <code>BuildAccelerationStructureFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 3 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureBuildPreference</code> | 合并 | <code>BuildAccelerationStructureFlags</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 4 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureCompactionQuery</code> | 合并 | <code>RaytracingAccelerationStructurePostbuildInfoDesc</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 5 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureCopyMode</code> | 改名 | <code>CopyAccelerationStructureMode</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 6 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureDescriptor</code> | 合并 | <code>WriteDescriptorSet</code> | descriptor write 使用 Vulkan 的一个 flat WriteDescriptorSet；不保留每资源一种 subclass。 |
| 7 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureInputs</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 8 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureRequirements</code> | 改名 | <code>AccelerationStructureBuildSizesInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 9 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureType</code> | 保留 | <code>AccelerationStructureType</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 10 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccelerationStructureView</code> | 改名 | <code>AccelerationStructure</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 11 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AccessEffect</code> | 改名 | <code>AccessFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 12 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AcquiredSwapchainImage</code> | 删除 | <code>—</code> | AcquireNextImage 返回 image index/Texture 与 GraphicsFence；不创建第二个 swapchain-image owner。 |
| 13 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AddressMode</code> | 保留 | <code>AddressMode</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 14 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.AliasingBarrier</code> | 合并 | <code>BarrierGroup</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 15 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BarrierKind</code> | 删除 | <code>—</code> | Kind 与具体 barrier 类型重复；flat barrier 不需要第二个判别真相。 |
| 16 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BarrierPhase</code> | 改名 | <code>BarrierFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 17 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindGroup</code> | 改名 | <code>DescriptorSet</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 18 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindGroupBinding</code> | 改名 | <code>DescriptorSetLayoutBinding</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 19 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindGroupLayout</code> | 改名 | <code>DescriptorSetLayout</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 20 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindlessSlot</code> | 改名 | <code>DescriptorHandle</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 21 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindlessTable</code> | 改名 | <code>DescriptorHeap</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 22 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BindlessTableDesc</code> | 改名 | <code>DescriptorHeapDesc</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 23 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BlendAttachment</code> | 保留 | <code>BlendAttachment</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 24 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BlendFactor</code> | 保留 | <code>BlendFactor</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 25 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BlendOperation</code> | 保留 | <code>BlendOperation</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 26 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BottomLevelAccelerationStructureInputs</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 27 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Buffer</code> | 保留 | <code>Buffer</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 28 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferDesc</code> | 保留 | <code>BufferDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 29 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferDescriptor</code> | 合并 | <code>WriteDescriptorSet</code> | descriptor write 使用 Vulkan 的一个 flat WriteDescriptorSet；不保留每资源一种 subclass。 |
| 30 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferMapMode</code> | 改名 | <code>MapMode</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 31 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferMapping</code> | 保留 | <code>BufferMapping</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 32 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferRange</code> | 保留 | <code>BufferRange</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 33 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferTextureCopy</code> | 合并 | <code>BufferImageCopy</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 34 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferTransitionBarrier</code> | 合并 | <code>BufferBarrier</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 35 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferUnorderedAccessBarrier</code> | 合并 | <code>BufferBarrier</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 36 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferUsage</code> | 保留 | <code>BufferUsage</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 37 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferView</code> | 保留 | <code>BufferView</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 38 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.BufferViewDesc</code> | 保留 | <code>BufferViewDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 39 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ColorAttachmentResolve</code> | 合并 | <code>RenderingAttachmentInfo</code> | 颜色、深度、模板和 resolve 都是同一 attachment info，不再各包一层。 |
| 40 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ColorWriteMask</code> | 保留 | <code>ColorWriteMask</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 41 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.CommandList</code> | 保留 | <code>CommandList</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 42 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.CompareOperation</code> | 保留 | <code>CompareOperation</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 43 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ComputePipelineDesc</code> | 保留 | <code>ComputePipelineDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 44 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.CullMode</code> | 保留 | <code>CullMode</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 45 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DepthAttachmentPayload</code> | 合并 | <code>RenderingAttachmentInfo</code> | 颜色、深度、模板和 resolve 都是同一 attachment info，不再各包一层。 |
| 46 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DepthStencil</code> | 改名 | <code>DepthStencilState</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 47 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DescriptorWrite</code> | 合并 | <code>WriteDescriptorSet</code> | descriptor write 使用 Vulkan 的一个 flat WriteDescriptorSet；不保留每资源一种 subclass。 |
| 48 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceCapabilities</code> | 保留 | <code>DeviceCapabilities</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 49 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceDomain</code> | 删除 | <code>—</code> | generation/domain 仅为 wrapper 与 backend owner 双层模型服务；owner 合并后全部消失。 |
| 50 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceDomainAccess</code> | 删除 | <code>—</code> | generation/domain 仅为 wrapper 与 backend owner 双层模型服务；owner 合并后全部消失。 |
| 51 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceError</code> | 删除 | <code>—</code> | DeviceError 与 DebugMessage 保存同一错误两遍；统一为 DebugMessage/异常与 native result。 |
| 52 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceErrorKind</code> | 删除 | <code>—</code> | DeviceError 与 DebugMessage 保存同一错误两遍；统一为 DebugMessage/异常与 native result。 |
| 53 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DeviceLimits</code> | 保留 | <code>DeviceLimits</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 54 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DevicePosition</code> | 删除 | <code>—</code> | 多个 queue completion 直接使用 GraphicsFence span；不再把集合升级成 DevicePosition/Wait owner。 |
| 55 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DevicePosition.Enumerator</code> | 删除 | <code>—</code> | 多个 queue completion 直接使用 GraphicsFence span；不再把集合升级成 DevicePosition/Wait owner。 |
| 56 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DevicePositionWait</code> | 删除 | <code>—</code> | 多个 queue completion 直接使用 GraphicsFence span；不再把集合升级成 DevicePosition/Wait owner。 |
| 57 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DispatchArguments</code> | 保留 | <code>DispatchArguments</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 58 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DrawIndexedIndirectArguments</code> | 改名 | <code>DrawIndexedArguments</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 59 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.DrawIndirectArguments</code> | 改名 | <code>DrawArguments</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 60 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FillMode</code> | 保留 | <code>FillMode</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 61 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FilterMode</code> | 保留 | <code>FilterMode</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 62 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Format</code> | 保留 | <code>Format</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 63 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FormatBlockLayout</code> | 保留 | <code>FormatBlockLayout</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 64 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FormatExtensions</code> | 保留 | <code>FormatExtensions</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 65 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FormatSupport</code> | 保留 | <code>FormatSupport</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 66 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.FrontFace</code> | 保留 | <code>FrontFace</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 67 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.GraphicsDiagnostic</code> | 改名 | <code>DebugMessage</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 68 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.GraphicsDiagnosticSeverity</code> | 改名 | <code>DebugMessageSeverity</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 69 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Heap</code> | 保留 | <code>Heap</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 70 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.HeapDesc</code> | 保留 | <code>HeapDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 71 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBindGroupLayoutOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 72 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBindGroupOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 73 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBindlessSlotOwner</code> | 删除 | <code>—</code> | DescriptorHandle 由 DescriptorHeap 直接分配和回收，不保留 slot-owner 接口。 |
| 74 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBindlessTableOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 75 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBufferOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 76 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IBufferViewOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 77 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ICommandRecorder</code> | 删除 | <code>—</code> | 唯一 CommandList 同时承担 recording 与 Close；删除 recorder/finished/native capability 纵向接口。 |
| 78 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IDevice</code> | 合并 | <code>Device</code> | public Device 改为唯一抽象 owner；现有接口不再形成额外层。 |
| 79 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IDeviceDomainSource</code> | 删除 | <code>—</code> | generation/domain 仅为 wrapper 与 backend owner 双层模型服务；owner 合并后全部消失。 |
| 80 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IFinishedCommandList</code> | 删除 | <code>—</code> | 唯一 CommandList 同时承担 recording 与 Close；删除 recorder/finished/native capability 纵向接口。 |
| 81 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IHeapOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 82 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.INativeCommandRecorder</code> | 删除 | <code>—</code> | 唯一 CommandList 同时承担 recording 与 Close；删除 recorder/finished/native capability 纵向接口。 |
| 83 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IndexFormat</code> | 保留 | <code>IndexFormat</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 84 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.InitialAccelerationStructureBuild</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 85 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IPipelineLayoutOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 86 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IPipelineOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 87 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IQueryPoolOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 88 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IQueueBackend</code> | 删除 | <code>—</code> | Queue 直接由 backend subclass 实现；IQueueBackend 转发层删除。 |
| 89 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ISamplerOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 90 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ISwapchainImageOwner</code> | 删除 | <code>—</code> | AcquireNextImage 返回 image index/Texture 与 GraphicsFence；不创建第二个 swapchain-image owner。 |
| 91 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ISwapchainOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 92 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ITextureOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 93 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ITextureViewOwner</code> | 删除 | <code>—</code> | backend 实体直接继承这些 public owner；全部 I*Owner 转发接口删除。 |
| 94 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ITransientResourceAllocator</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 95 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ITransientResourceSource</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 96 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.IWorkGraphOwner</code> | 删除 | <code>—</code> | backend owner 直接继承 WorkGraph；不再由 IWorkGraphOwner 回调释放。 |
| 97 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.LoadAction</code> | 保留 | <code>LoadAction</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 98 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.MemoryBudget</code> | 保留 | <code>MemoryBudget</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 99 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.MemoryType</code> | 保留 | <code>MemoryType</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 100 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.MeshPipelineDesc</code> | 保留 | <code>MeshPipelineDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 101 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.MeshShaderTier</code> | 保留 | <code>MeshShaderTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 102 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PhysicalAllocationId</code> | 删除 | <code>—</code> | 真实 placement 直接引用 Heap、offset、size；不再发明 allocation id/placement 中间身份。 |
| 103 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PhysicalPlacement</code> | 删除 | <code>—</code> | 真实 placement 直接引用 Heap、offset、size；不再发明 allocation id/placement 中间身份。 |
| 104 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Pipeline</code> | 保留 | <code>Pipeline</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 105 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineBindingPolicy</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 106 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineBindingResult</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 107 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineCacheKey</code> | 保留 | <code>PipelineCacheKey</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 108 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineCacheStatistics</code> | 保留 | <code>PipelineCacheStatistics</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 109 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineCompilationStatistics</code> | 保留 | <code>PipelineCompilationStatistics</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 110 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineDescriptorKey</code> | 合并 | <code>PipelineCacheKey</code> | 两个 pipeline key 系统合为一个 cache key；静态转发容器内联。 |
| 111 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineDescriptorKeys</code> | 合并 | <code>PipelineCacheKey</code> | 两个 pipeline key 系统合为一个 cache key；静态转发容器内联。 |
| 112 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineLayout</code> | 保留 | <code>PipelineLayout</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 113 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineLayout.OccupiedRange</code> | 改名 | <code>DescriptorRange</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 114 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineLayout.ShaderRegisterClass</code> | 改名 | <code>DescriptorRangeType</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 115 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineLayoutDesc</code> | 改名 | <code>PipelineLayoutCreateInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 116 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineStatistics</code> | 保留 | <code>PipelineStatistics</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 117 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineStatus</code> | 保留 | <code>PipelineStatus</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 118 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineType</code> | 删除 | <code>—</code> | Pipeline 的实际 owner/创建入口已经区分类型，不再维护可漂移的 PipelineType。 |
| 119 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PipelineWarmupResult</code> | 保留 | <code>PipelineWarmupResult</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 120 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PresentOptions</code> | 改名 | <code>PresentInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 121 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PresentResult</code> | 保留 | <code>PresentResult</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 122 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PrimitiveTopology</code> | 保留 | <code>PrimitiveTopology</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 123 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.PushConstantBinding</code> | 改名 | <code>PushConstantRange</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 124 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueryPool</code> | 保留 | <code>QueryPool</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 125 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueryPoolDesc</code> | 改名 | <code>QueryPoolCreateInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 126 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueryType</code> | 保留 | <code>QueryType</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 127 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Queue</code> | 保留 | <code>Queue</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 128 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueuePosition</code> | 改名 | <code>GraphicsFence</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 129 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueueSupport</code> | 改名 | <code>QueueFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 130 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.QueueType</code> | 保留 | <code>QueueType</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 131 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Rasterizer</code> | 改名 | <code>RasterizerState</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 132 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RasterPipelineDesc</code> | 保留 | <code>RasterPipelineDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 133 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayDispatch</code> | 改名 | <code>DispatchRaysDesc</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 134 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingAabbGeometry</code> | 改名 | <code>AccelerationStructureGeometryAabbsData</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 135 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingGeometryFlags</code> | 改名 | <code>GeometryFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 136 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingHitGroup</code> | 改名 | <code>HitGroupDesc</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 137 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingHitGroupType</code> | 改名 | <code>HitGroupType</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 138 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingLibrary</code> | 改名 | <code>DxilLibrary</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 139 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingLocalRootAssociation</code> | 改名 | <code>SubobjectToExportsAssociation</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 140 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingPipelineDesc</code> | 改名 | <code>RayTracingPipelineCreateInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 141 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingShaderRecord</code> | 改名 | <code>ShaderRecord</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 142 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingShaderTable</code> | 改名 | <code>ShaderBindingTable</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 143 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingTier</code> | 保留 | <code>RayTracingTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 144 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RayTracingTriangleGeometry</code> | 改名 | <code>AccelerationStructureGeometryTrianglesData</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 145 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RenderingColorAttachment</code> | 合并 | <code>RenderingAttachmentInfo</code> | 颜色、深度、模板和 resolve 都是同一 attachment info，不再各包一层。 |
| 146 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RenderingContinuationKey</code> | 合并 | <code>RenderingInfo</code> | 跨 command list 的 rendering continuation 直接保存规范化 RenderingInfo；不再另建 Key。 |
| 147 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RenderingDepthStencilAttachment</code> | 合并 | <code>RenderingAttachmentInfo</code> | 颜色、深度、模板和 resolve 都是同一 attachment info，不再各包一层。 |
| 148 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RenderingFlags</code> | 保留 | <code>RenderingFlags</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 149 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RenderingSetup</code> | 改名 | <code>RenderingInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 150 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.RequireReadyPipelineBinding</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 151 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResidencyPriority</code> | 保留 | <code>ResidencyPriority</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 152 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResidencyTrimResult</code> | 删除 | <code>—</code> | 结果只包装一个数字/状态；直接返回被回收字节数及完成 fence。 |
| 153 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResolveMode</code> | 保留 | <code>ResolveMode</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 154 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Resource</code> | 保留 | <code>Resource</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 155 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceBarrier</code> | 合并 | <code>BarrierGroup</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 156 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceHeapClass</code> | 删除 | <code>—</code> | heap compatibility 由 MemoryRequirements/HeapFlags 给出；不再复制资源种类判别器。 |
| 157 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceHeapTier</code> | 保留 | <code>ResourceHeapTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 158 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceKind</code> | 删除 | <code>—</code> | 静态/运行时 owner 类型已经区分 Buffer 与 Texture，不再保存 ResourceKind。 |
| 159 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceRequirements</code> | 改名 | <code>MemoryRequirements</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 160 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ResourceState</code> | 保留 | <code>ResourceState</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 161 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Sampler</code> | 保留 | <code>Sampler</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 162 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerBorderColor</code> | 改名 | <code>BorderColor</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 163 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerDesc</code> | 保留 | <code>SamplerDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 164 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerDescriptor</code> | 合并 | <code>WriteDescriptorSet</code> | descriptor write 使用 Vulkan 的一个 flat WriteDescriptorSet；不保留每资源一种 subclass。 |
| 165 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerFeedbackMapDesc</code> | 删除 | <code>—</code> | Sampler Feedback 是 D3D12 backend 扩展；portable command surface 不保留自造 map/mode/layout 包装。 |
| 166 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerFeedbackMapLayout</code> | 删除 | <code>—</code> | Sampler Feedback 是 D3D12 backend 扩展；portable command surface 不保留自造 map/mode/layout 包装。 |
| 167 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerFeedbackMode</code> | 删除 | <code>—</code> | Sampler Feedback 是 D3D12 backend 扩展；portable command surface 不保留自造 map/mode/layout 包装。 |
| 168 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SamplerFeedbackTier</code> | 保留 | <code>SamplerFeedbackTier</code> | D3D12 已有同名 capability tier；仅作为 capability fact 保留。 |
| 169 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ScissorRect</code> | 改名 | <code>Rect2D</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 170 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderArtifact</code> | 改名 | <code>ShaderModule</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 171 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderArtifactKey</code> | 合并 | <code>ShaderModuleIdentifier</code> | shader reflection 与 descriptor layout 只保留一份 binding 真相。 |
| 172 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderBinaryFormat</code> | 保留 | <code>ShaderBinaryFormat</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 173 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderInterface</code> | 合并 | <code>ShaderReflection</code> | shader reflection 与 descriptor layout 只保留一份 binding 真相。 |
| 174 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderQualifiers</code> | 合并 | <code>ShaderReflection</code> | shader reflection 与 descriptor layout 只保留一份 binding 真相。 |
| 175 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderRegisterAddress</code> | 合并 | <code>ShaderReflection</code> | shader reflection 与 descriptor layout 只保留一份 binding 真相。 |
| 176 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderSlot</code> | 合并 | <code>DescriptorSetLayoutBinding</code> | shader reflection 与 descriptor layout 只保留一份 binding 真相。 |
| 177 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderSlotKind</code> | 改名 | <code>DescriptorType</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 178 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderStage</code> | 保留 | <code>ShaderStage</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 179 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderStages</code> | 改名 | <code>ShaderStageFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 180 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShaderTableRegion</code> | 改名 | <code>StridedDeviceAddressRegion</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 181 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShadingRate</code> | 保留 | <code>ShadingRate</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 182 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.ShadingRateCombiner</code> | 保留 | <code>ShadingRateCombiner</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 183 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SkipPendingPipelineBinding</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 184 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseBoxTileRegion</code> | 合并 | <code>TileRegionSize</code> | D3D12 的一个 TileRegionSize 同时表示线性数量和 box。 |
| 185 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseLinearTileRegion</code> | 合并 | <code>TileRegionSize</code> | D3D12 的一个 TileRegionSize 同时表示线性数量和 box。 |
| 186 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseMappedTileMapping</code> | 合并 | <code>TileMapping</code> | 映射/取消映射由一个 TileMapping + TileRangeFlags 表达，不保留继承树。 |
| 187 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparsePackedMips</code> | 改名 | <code>PackedMipInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 188 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseResourceTier</code> | 保留 | <code>SparseResourceTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 189 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseSubresourceTiling</code> | 改名 | <code>SubresourceTiling</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 190 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseTileCoordinate</code> | 改名 | <code>TiledResourceCoordinate</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 191 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseTileMapping</code> | 合并 | <code>TileMapping</code> | 映射/取消映射由一个 TileMapping + TileRangeFlags 表达，不保留继承树。 |
| 192 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseTileRegion</code> | 合并 | <code>TileRegionSize</code> | D3D12 的一个 TileRegionSize 同时表示线性数量和 box。 |
| 193 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseTileShape</code> | 改名 | <code>TileShape</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 194 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SparseUnmappedTileMapping</code> | 合并 | <code>TileMapping</code> | 映射/取消映射由一个 TileMapping + TileRangeFlags 表达，不保留继承树。 |
| 195 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.StencilAttachmentPayload</code> | 合并 | <code>RenderingAttachmentInfo</code> | 颜色、深度、模板和 resolve 都是同一 attachment info，不再各包一层。 |
| 196 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.StencilFace</code> | 保留 | <code>StencilFace</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 197 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.StencilOperation</code> | 保留 | <code>StencilOperation</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 198 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.StoreAction</code> | 保留 | <code>StoreAction</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 199 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Swapchain</code> | 保留 | <code>Swapchain</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 200 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SwapchainColorSpace</code> | 改名 | <code>ColorSpace</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 201 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SwapchainDesc</code> | 改名 | <code>SwapchainCreateInfo</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 202 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.SwapchainPresentMode</code> | 改名 | <code>PresentMode</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 203 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Texture</code> | 保留 | <code>Texture</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 204 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureBufferCopy</code> | 合并 | <code>BufferImageCopy</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 205 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureBufferLayout</code> | 合并 | <code>BufferImageCopy</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 206 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureCopyFootprint</code> | 改名 | <code>PlacedSubresourceFootprint</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 207 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureCopyRegion</code> | 合并 | <code>BufferImageCopy / ImageCopy</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 208 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureDesc</code> | 保留 | <code>TextureDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 209 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureDescriptor</code> | 合并 | <code>WriteDescriptorSet</code> | descriptor write 使用 Vulkan 的一个 flat WriteDescriptorSet；不保留每资源一种 subclass。 |
| 210 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureDimension</code> | 保留 | <code>TextureDimension</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 211 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TexturePlane</code> | 改名 | <code>ImageAspect</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 212 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TexturePlanes</code> | 改名 | <code>ImageAspectFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 213 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureResolveRegion</code> | 合并 | <code>ImageResolve</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 214 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureSampleType</code> | 保留 | <code>TextureSampleType</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 215 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureSubresourceRange</code> | 改名 | <code>SubresourceRange</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 216 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureToTextureCopy</code> | 合并 | <code>ImageCopy</code> | 方向相反但字段同构的复制参数只保留 Vulkan 的一个标准结构。 |
| 217 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureTransitionBarrier</code> | 合并 | <code>TextureBarrier</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 218 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureUnorderedAccessBarrier</code> | 合并 | <code>TextureBarrier</code> | 删除 barrier 基类、Kind 判别器和每变体一类的继承树；只保留标准 flat barrier 数据。 |
| 219 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureUsage</code> | 保留 | <code>TextureUsage</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 220 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureView</code> | 保留 | <code>TextureView</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 221 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureViewDesc</code> | 保留 | <code>TextureViewDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 222 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureViewDimension</code> | 保留 | <code>TextureViewDimension</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 223 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureViewFormats</code> | 删除 | <code>—</code> | 允许 view format 直接是 create-info 中的 span/immutable array；删除自造集合及 Enumerator。 |
| 224 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureViewFormats.Enumerator</code> | 删除 | <code>—</code> | 允许 view format 直接是 create-info 中的 span/immutable array；删除自造集合及 Enumerator。 |
| 225 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TextureViewUsage</code> | 保留 | <code>TextureViewUsage</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 226 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TimestampCalibration</code> | 改名 | <code>ClockCalibration</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 227 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TopLevelAccelerationStructureInputs</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 228 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientBufferClaim</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 229 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientClaimState</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 230 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientHeapClaim</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 231 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator</code> | 保留 | <code>TransientResourceAllocator</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 232 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.BufferEntry</code> | 保留 | <code>TransientResourceAllocator.BufferEntry</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 233 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.BufferKey</code> | 保留 | <code>TransientResourceAllocator.BufferKey</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 234 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.BufferViewEntry</code> | 保留 | <code>TransientResourceAllocator.BufferViewEntry</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 235 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.BufferViewKey</code> | 保留 | <code>TransientResourceAllocator.BufferViewKey</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 236 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.HeapEntry</code> | 保留 | <code>TransientResourceAllocator.HeapEntry</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 237 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.TextureEntry</code> | 保留 | <code>TransientResourceAllocator.TextureEntry</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 238 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.TextureKey</code> | 保留 | <code>TransientResourceAllocator.TextureKey</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 239 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.TextureViewEntry</code> | 保留 | <code>TransientResourceAllocator.TextureViewEntry</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 240 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceAllocator.TextureViewKey</code> | 保留 | <code>TransientResourceAllocator.TextureViewKey</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 241 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientResourceSource</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 242 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.TransientTextureClaim</code> | 删除 | <code>—</code> | TransientResourceAllocator 是唯一 pool；source/allocator 接口和 claim generation 协议都是重复纵向层。 |
| 243 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.UpdateAccelerationStructureBuild</code> | 合并 | <code>AccelerationStructureBuildGeometryInfo</code> | Vulkan 的一个 AccelerationStructureBuildGeometryInfo 已包含 mode/src/dst/scratch/geometry。 |
| 244 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.UseFallbackPipelineBinding</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 245 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.VariableRateShadingTier</code> | 保留 | <code>VariableRateShadingTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 246 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.VertexAttribute</code> | 保留 | <code>VertexAttribute</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 247 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.VertexBufferLayout</code> | 保留 | <code>VertexBufferLayout</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 248 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.Viewport</code> | 保留 | <code>Viewport</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 249 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WaitForPendingPipelineBinding</code> | 删除 | <code>—</code> | pipeline ready/pending/fallback 是 cache 决策，不是 command binding 的类层级。 |
| 250 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraph</code> | 保留 | <code>WorkGraph</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 251 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphBackingOperation</code> | 改名 | <code>SetWorkGraphFlags</code> | 改用 Vulkan/D3D12/UE/Unity/Filament 中已出现的精确术语。 |
| 252 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphBufferAccess</code> | 合并 | <code>AccessFlags + BufferHandle</code> | Work Graph 资源访问由普通 RG access/barrier 声明承担，不是 DispatchGraph 的额外参数。 |
| 253 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphDesc</code> | 保留 | <code>WorkGraphDesc</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 254 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphEntrypointLayout</code> | 合并 | <code>WorkGraph</code> | Work Graph 资源访问由普通 RG access/barrier 声明承担，不是 DispatchGraph 的额外参数。 |
| 255 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphMemoryRequirements</code> | 保留 | <code>WorkGraphMemoryRequirements</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 256 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphTextureAccess</code> | 合并 | <code>AccessFlags + TextureHandle</code> | Work Graph 资源访问由普通 RG access/barrier 声明承担，不是 DispatchGraph 的额外参数。 |
| 257 | <code>SomeEngine.Graphics</code> | <code>SomeEngine.Graphics.WorkGraphTier</code> | 保留 | <code>WorkGraphTier</code> | 外部图形 API/引擎已有同名概念；当前节点承担唯一事实或 owner。 |
| 258 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.BindingLookupRow</code> | 合并 | <code>DescriptorRange</code> | layout lookup 与 D3D12 descriptor range 是同一事实。 |
| 259 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.BufferCommandResourceRegion</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 260 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.BufferIntervalKey</code> | 合并 | <code>BufferRange</code> | buffer interval key 与已有 BufferRange 同义。 |
| 261 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandAllocation</code> | 删除 | <code>—</code> | CommandRecorder、CommandAllocation、NativeCommandList 原为同一 command list 的三层 owner；只留 D3D12CommandList。 |
| 262 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandDescriptorArena</code> | 删除 | <code>—</code> | descriptor arena 是 command-list 私有分配细节；字段内联，不升级成额外 owner。 |
| 263 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandDescriptorArena.HeapPair</code> | 删除 | <code>—</code> | descriptor arena 是 command-list 私有分配细节；字段内联，不升级成额外 owner。 |
| 264 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder</code> | 删除 | <code>—</code> | CommandRecorder、CommandAllocation、NativeCommandList 原为同一 command list 的三层 owner；只留 D3D12CommandList。 |
| 265 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.BoundColorAttachment</code> | 合并 | <code>RenderingAttachmentInfo</code> | bound attachment 与 native barrier 临时结构直接使用标准 RenderingAttachmentInfo/BarrierGroup。 |
| 266 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.BoundDepthStencilAttachment</code> | 合并 | <code>RenderingAttachmentInfo</code> | bound attachment 与 native barrier 临时结构直接使用标准 RenderingAttachmentInfo/BarrierGroup。 |
| 267 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.DescriptorGroupBorrow</code> | 删除 | <code>—</code> | descriptor arena 是 command-list 私有分配细节；字段内联，不升级成额外 owner。 |
| 268 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.NativeBarrierGroup</code> | 合并 | <code>BarrierGroup</code> | bound attachment 与 native barrier 临时结构直接使用标准 RenderingAttachmentInfo/BarrierGroup。 |
| 269 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.NativeBufferBarrier</code> | 合并 | <code>BufferBarrier</code> | bound attachment 与 native barrier 临时结构直接使用标准 RenderingAttachmentInfo/BarrierGroup。 |
| 270 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.NativeTextureBarrier</code> | 合并 | <code>TextureBarrier</code> | bound attachment 与 native barrier 临时结构直接使用标准 RenderingAttachmentInfo/BarrierGroup。 |
| 271 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandRecorder.RayTracingRootConstantWrite</code> | 删除 | <code>—</code> | ray tracing descriptor/root-constant staging 移到 ShaderBindingTable 构建；不是独立 command state 类型。 |
| 272 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandResourceMutation</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 273 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CommandResourceRegion</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 274 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CpuDescriptorPool</code> | 保留 | <code>CpuDescriptorPool</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 275 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CpuDescriptorPool.Bucket</code> | 保留 | <code>CpuDescriptorPool.Bucket</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 276 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.CpuDescriptorPool.Page</code> | 保留 | <code>CpuDescriptorPool.Page</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 277 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.DescriptorBlock</code> | 删除 | <code>—</code> | descriptor arena 是 command-list 私有分配细节；字段内联，不升级成额外 owner。 |
| 278 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device</code> | 改名 | <code>D3D12Device</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 279 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.BufferRequirementKey</code> | 删除 | <code>—</code> | requirement cache 直接以规范化 BufferDesc/TextureDesc 为 key。 |
| 280 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.DxilLibraryDescriptionNative</code> | 删除 | <code>—</code> | 这些 private *Native struct 是对 D3D12 interop struct 的第二次手写镜像。 |
| 281 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.IndirectSignatureKey</code> | 保留 | <code>Device.IndirectSignatureKey</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 282 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.MeshPipelineStream</code> | 改名 | <code>PipelineStateStream</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 283 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.NativeBufferMapping</code> | 合并 | <code>BufferMapping</code> | public BufferMapping 已经是唯一 mapping lifetime；backend 对象并入它。 |
| 284 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.PipelineCacheEntry</code> | 保留 | <code>Device.PipelineCacheEntry</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 285 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.PipelineCacheEntryKey</code> | 保留 | <code>Device.PipelineCacheEntryKey</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 286 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.RetiredAllocation</code> | 保留 | <code>Device.RetiredAllocation</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 287 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.StateObjectDescriptionNative</code> | 删除 | <code>—</code> | 这些 private *Native struct 是对 D3D12 interop struct 的第二次手写镜像。 |
| 288 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.StateSubObjectNative</code> | 删除 | <code>—</code> | 这些 private *Native struct 是对 D3D12 interop struct 的第二次手写镜像。 |
| 289 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.TextureRequirementKey</code> | 删除 | <code>—</code> | requirement cache 直接以规范化 BufferDesc/TextureDesc 为 key。 |
| 290 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.Device.WorkGraphDescriptionNative</code> | 删除 | <code>—</code> | 这些 private *Native struct 是对 D3D12 interop struct 的第二次手写镜像。 |
| 291 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.DeviceConfiguration</code> | 保留 | <code>DeviceConfiguration</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 292 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.DxilProgramHeader</code> | 保留 | <code>DxilProgramHeader</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 293 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.EntityKey</code> | 删除 | <code>—</code> | wrapper/generation owner 删除后不再需要 slot+generation EntityTable。 |
| 294 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.EntityTable&lt;T&gt;</code> | 删除 | <code>—</code> | wrapper/generation owner 删除后不再需要 slot+generation EntityTable。 |
| 295 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.EntityTable&lt;T&gt;.Entry</code> | 删除 | <code>—</code> | wrapper/generation owner 删除后不再需要 slot+generation EntityTable。 |
| 296 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeAccelerationStructureBinding</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 297 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeAccelerationStructureBindlessValue</code> | 删除 | <code>—</code> | bindless value subclass 只是在 DescriptorHandle 外再包资源引用；合并进 DescriptorHeap entry。 |
| 298 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindGroup</code> | 改名 | <code>D3D12DescriptorSet</code> | BindGroup vocabulary 统一为 DescriptorSet vocabulary。 |
| 299 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindGroupLayout</code> | 改名 | <code>D3D12DescriptorSetLayout</code> | BindGroup vocabulary 统一为 DescriptorSet vocabulary。 |
| 300 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBinding</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 301 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindingPayload</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 302 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindlessEntry</code> | 改名 | <code>D3D12DescriptorHeap.Entry</code> | bindless 公共概念已统一为 DescriptorHeap/DescriptorHandle。 |
| 303 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindlessTable</code> | 改名 | <code>D3D12DescriptorHeap</code> | bindless 公共概念已统一为 DescriptorHeap/DescriptorHandle。 |
| 304 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBindlessValue</code> | 删除 | <code>—</code> | bindless value subclass 只是在 DescriptorHandle 外再包资源引用；合并进 DescriptorHeap entry。 |
| 305 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBuffer</code> | 改名 | <code>D3D12Buffer</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 306 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBuffer.ClearDescriptor</code> | 删除 | <code>—</code> | clear descriptor 是 D3D12Buffer 的一个字段，不是独立概念。 |
| 307 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBufferBinding</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 308 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBufferBindlessValue</code> | 删除 | <code>—</code> | bindless value subclass 只是在 DescriptorHandle 外再包资源引用；合并进 DescriptorHeap entry。 |
| 309 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeBufferView</code> | 改名 | <code>D3D12BufferView</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 310 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeCommandList</code> | 改名 | <code>D3D12CommandList</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 311 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeComputePipeline</code> | 改名 | <code>D3D12ComputePipeline</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 312 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeCpuDescriptor</code> | 改名 | <code>DescriptorAllocation</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 313 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeDevice</code> | 合并 | <code>D3D12Device</code> | public/backend Device 两层合成一个 D3D12Device。 |
| 314 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeDiagnosticDrain</code> | 改名 | <code>D3D12InfoQueue</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 315 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeFormat</code> | 删除 | <code>—</code> | 转换函数内联到 D3D12Device/D3D12CommandList；Native* 静态容器不是领域类型。 |
| 316 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeHeap</code> | 改名 | <code>D3D12Heap</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 317 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeLifetime</code> | 删除 | <code>—</code> | public owner 自己承担 lifetime；generic NativeLifetime 基类不再形成第二个 owner 层。 |
| 318 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeMeshPipeline</code> | 改名 | <code>D3D12MeshPipeline</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 319 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativePipeline</code> | 删除 | <code>—</code> | public Pipeline 已是 backend pipeline 的共同 owner；额外 NativePipeline root 删除。 |
| 320 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativePipelineLayout</code> | 改名 | <code>D3D12PipelineLayout</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 321 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativePipelineLibrary</code> | 改名 | <code>D3D12PipelineLibrary</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 322 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativePipelineShader</code> | 合并 | <code>ShaderBytecode</code> | shader bytecode/root binding 直接用 D3D12 标准结构。 |
| 323 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeQueryPool</code> | 改名 | <code>D3D12QueryPool</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 324 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeQueue</code> | 改名 | <code>D3D12Queue</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 325 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeQueueType</code> | 删除 | <code>—</code> | 转换函数内联到 D3D12Device/D3D12CommandList；Native* 静态容器不是领域类型。 |
| 326 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeRasterization</code> | 删除 | <code>—</code> | 转换函数内联到 D3D12Device/D3D12CommandList；Native* 静态容器不是领域类型。 |
| 327 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeRasterPipeline</code> | 改名 | <code>D3D12RasterPipeline</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 328 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeRayTracingPipeline</code> | 改名 | <code>D3D12RayTracingPipeline</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 329 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeResourceSynchronization</code> | 删除 | <code>—</code> | 转换函数内联到 D3D12Device/D3D12CommandList；Native* 静态容器不是领域类型。 |
| 330 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeRootBinding</code> | 合并 | <code>RootDescriptorTable</code> | shader bytecode/root binding 直接用 D3D12 标准结构。 |
| 331 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeRootConstant</code> | 合并 | <code>RootConstants</code> | shader bytecode/root binding 直接用 D3D12 标准结构。 |
| 332 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeSampler</code> | 改名 | <code>D3D12Sampler</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 333 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeSamplerBinding</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 334 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeSamplerBindlessValue</code> | 删除 | <code>—</code> | bindless value subclass 只是在 DescriptorHandle 外再包资源引用；合并进 DescriptorHeap entry。 |
| 335 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeSwapchain</code> | 改名 | <code>D3D12Swapchain</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 336 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeTexture</code> | 改名 | <code>D3D12Texture</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 337 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeTextureBinding</code> | 删除 | <code>—</code> | NativeBinding/Payload 及资源 subclass 重复 WriteDescriptorSet；使用 flat descriptor write。 |
| 338 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeTextureBindlessValue</code> | 删除 | <code>—</code> | bindless value subclass 只是在 DescriptorHandle 外再包资源引用；合并进 DescriptorHeap entry。 |
| 339 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeTextureView</code> | 改名 | <code>D3D12TextureView</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 340 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.NativeWorkGraph</code> | 改名 | <code>D3D12WorkGraph</code> | backend 类型用显式 D3D12 owner 名；它们直接实现/继承 public owner。 |
| 341 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.OpaqueNativeAccessMutation</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 342 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.QueryAvailabilityMutation</code> | 删除 | <code>—</code> | query availability 是 QueryPool 状态，不是 command mutation 层。 |
| 343 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.ResourceAccessMutation</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 344 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool</code> | 保留 | <code>ShaderVisibleDescriptorPool</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 345 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.DescriptorAllocation</code> | 保留 | <code>ShaderVisibleDescriptorPool.DescriptorAllocation</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 346 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.RangeAllocator</code> | 保留 | <code>ShaderVisibleDescriptorPool.RangeAllocator</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 347 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.RangeAllocator.FreeRange</code> | 保留 | <code>ShaderVisibleDescriptorPool.RangeAllocator.FreeRange</code> | private cache/allocator/transaction helper 有独立算法职责，且名字是普通实现术语。 |
| 348 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.SplitBarrierBeginMutation</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 349 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.SplitBarrierEndMutation</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 350 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.TextureCommandResourceRegion</code> | 删除 | <code>—</code> | 一组 Mutation/Region subclass 只是 ResourceStateTracker 的事件表示；合并为一个 tracker。 |
| 351 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.WorkGraphDispatchUse</code> | 删除 | <code>—</code> | Work Graph 使用/初始化状态由 D3D12WorkGraph 与 D3D12CommandList 直接持有。 |
| 352 | <code>SomeEngine.Graphics.Direct3D12</code> | <code>SomeEngine.Graphics.Direct3D12.WorkGraphInitialization</code> | 删除 | <code>—</code> | Work Graph 使用/初始化状态由 D3D12WorkGraph 与 D3D12CommandList 直接持有。 |
| 353 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.AccelerationStructureKey</code> | 删除 | <code>—</code> | AS key/update-row 复制了标准 build geometry；状态并入 NullAccelerationStructure。 |
| 354 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.AccelerationStructureUpdateKey</code> | 删除 | <code>—</code> | AS key/update-row 复制了标准 build geometry；状态并入 NullAccelerationStructure。 |
| 355 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.AccelerationStructureUpdateKey.AabbUpdateRow</code> | 删除 | <code>—</code> | AS key/update-row 复制了标准 build geometry；状态并入 NullAccelerationStructure。 |
| 356 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.AccelerationStructureUpdateKey.TriangleUpdateRow</code> | 删除 | <code>—</code> | AS key/update-row 复制了标准 build geometry；状态并入 NullAccelerationStructure。 |
| 357 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BarrierPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 358 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BeginQueryPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 359 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BeginRenderingPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 360 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BindlessEntry</code> | 改名 | <code>NullDescriptorHeap.Entry</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 361 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BuildAccelerationStructurePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 362 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.BuiltAccelerationStructure</code> | 改名 | <code>NullAccelerationStructure</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 363 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ClearBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 364 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ClearDepthStencilTexturePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 365 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ClearSamplerFeedbackPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 366 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ClearTexturePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 367 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ClearUnorderedAccessBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 368 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CommandRecorder</code> | 合并 | <code>NullCommandList</code> | 唯一 NullCommandList 承担 recording/Close；CommandRecorder 不再是第二层。 |
| 369 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CommandUsageLedger</code> | 删除 | <code>—</code> | CommandList 直接持有所借 owner 集；不再经 UsageLedger snapshot 一次。 |
| 370 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CopyAccelerationStructurePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 371 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CopyBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 372 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CopyBufferToTexturePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 373 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CopyTexturePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 374 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.CopyTextureToBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 375 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DecodeSamplerFeedbackPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 376 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device</code> | 改名 | <code>NullDevice</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 377 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.BufferRequirementKey</code> | 删除 | <code>—</code> | requirement cache 直接用规范化 Desc；mapping backend state 直接并入 BufferMapping。 |
| 378 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.NullBufferMapping</code> | 合并 | <code>BufferMapping</code> | public BufferMapping 已是唯一映射 lifetime。 |
| 379 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction</code> | 保留 | <code>Device.SubmissionTransaction</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 380 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction.QueryCounters</code> | 保留 | <code>Device.SubmissionTransaction.QueryCounters</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 381 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction.StagedBuffer</code> | 保留 | <code>Device.SubmissionTransaction.StagedBuffer</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 382 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction.StagedQueryPool</code> | 保留 | <code>Device.SubmissionTransaction.StagedQueryPool</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 383 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction.StagedStorage</code> | 保留 | <code>Device.SubmissionTransaction.StagedStorage</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 384 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.SubmissionTransaction.StagedTexture</code> | 保留 | <code>Device.SubmissionTransaction.StagedTexture</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 385 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.Device.TextureRequirementKey</code> | 删除 | <code>—</code> | requirement cache 直接用规范化 Desc；mapping backend state 直接并入 BufferMapping。 |
| 386 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DeviceConfiguration</code> | 保留 | <code>DeviceConfiguration</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 387 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchIndirectPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 388 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchMeshIndirectPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 389 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchMeshPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 390 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 391 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchRaysPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 392 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DispatchWorkGraphPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 393 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DrawIndexedIndirectPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 394 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DrawIndexedPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 395 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DrawIndirectPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 396 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.DrawPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 397 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EmitAccelerationStructureCompactedSizePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 398 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EncodeSamplerFeedbackPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 399 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EndQueryPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 400 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EndRenderingPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 401 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EntityTable&lt;T&gt;</code> | 删除 | <code>—</code> | public owner 与 backend owner 合并后，不再需要 EntityTable/Slot/NullPosition generation 系统。 |
| 402 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.EntityTable&lt;T&gt;.Slot</code> | 删除 | <code>—</code> | public owner 与 backend owner 合并后，不再需要 EntityTable/Slot/NullPosition generation 系统。 |
| 403 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.InsertDebugMarkerPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 404 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullAccelerationStructureBinding</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 405 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullAccelerationStructureBindlessValue</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 406 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBindGroup</code> | 改名 | <code>NullDescriptorSet</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 407 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBindGroupLayout</code> | 改名 | <code>NullDescriptorSetLayout</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 408 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBinding</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 409 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBindlessTable</code> | 改名 | <code>NullDescriptorHeap</code> | Null backend owner 用显式且常见的 Null* 名，并直接继承 public owner。 |
| 410 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBindlessValue</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 411 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBuffer</code> | 保留 | <code>NullBuffer</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 412 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBufferBinding</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 413 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBufferBindlessValue</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 414 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullBufferView</code> | 保留 | <code>NullBufferView</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 415 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullCommandList</code> | 保留 | <code>NullCommandList</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 416 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullDeviceStatistics</code> | 保留 | <code>NullDeviceStatistics</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 417 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullHeap</code> | 保留 | <code>NullHeap</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 418 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullPipeline</code> | 保留 | <code>NullPipeline</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 419 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullPipelineLayout</code> | 保留 | <code>NullPipelineLayout</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 420 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullPosition</code> | 删除 | <code>—</code> | public owner 与 backend owner 合并后，不再需要 EntityTable/Slot/NullPosition generation 系统。 |
| 421 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullQueryPool</code> | 保留 | <code>NullQueryPool</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 422 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullRayTracingPipeline</code> | 删除 | <code>—</code> | ray-tracing 状态并入 NullPipeline；不再创建第二个 pipeline owner。 |
| 423 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullSampler</code> | 保留 | <code>NullSampler</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 424 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullSamplerBinding</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 425 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullSamplerBindlessValue</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 426 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullSwapchain</code> | 保留 | <code>NullSwapchain</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 427 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullTexture</code> | 保留 | <code>NullTexture</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 428 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullTextureBinding</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 429 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullTextureBindlessValue</code> | 删除 | <code>—</code> | descriptor binding/value subclass 重复 WriteDescriptorSet/DescriptorHeap entry。 |
| 430 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullTextureView</code> | 保留 | <code>NullTextureView</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 431 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.NullWorkGraph</code> | 保留 | <code>NullWorkGraph</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 432 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.PendingSplitTransition</code> | 删除 | <code>—</code> | split transition 由 BufferBarrier/TextureBarrier + BarrierFlags 的 tracker 直接保存。 |
| 433 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.PopDebugGroupPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 434 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.PushDebugGroupPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 435 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ResetQueryPoolPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 436 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ResolveQueryPoolPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 437 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.ResolveTexturePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 438 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.RetainedCommand</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 439 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.RetainedCommandKind</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 440 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.RetainedCommandStream</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 441 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetBindGroupPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 442 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetDescriptorsPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 443 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetIndexBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 444 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetPipelinePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 445 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetPushConstantsPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 446 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetScissorPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 447 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetShadingRateImagePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 448 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetShadingRatePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 449 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetStencilReferencePayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 450 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetVertexBufferPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 451 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.SetViewportPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 452 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.TextureLayout</code> | 保留 | <code>TextureLayout</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 453 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.TexturePlaneEnumerator</code> | 保留 | <code>TexturePlaneEnumerator</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 454 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.TextureSubresourceEnumerator</code> | 保留 | <code>TextureSubresourceEnumerator</code> | Null backend 的真实 owner、原子提交模拟或布局算法有独立职责。 |
| 455 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.WorkGraphBackingRegistration</code> | 删除 | <code>—</code> | backing registration 是 NullWorkGraph 的状态，不是独立领域类型。 |
| 456 | <code>SomeEngine.Graphics.Null</code> | <code>SomeEngine.Graphics.Null.WriteTimestampPayload</code> | 删除 | <code>—</code> | Null command payload/Kind/boxed stream 是一命令一类型的重复层；合并为 Filament 式生成 CommandStream。 |
| 457 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccelerationStructureBuildAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 458 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccelerationStructureFacts</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 459 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccelerationStructureInputsAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 460 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccelerationStructureRow</code> | 删除 | <code>—</code> | resource/view canonical storage 直接复用 Desc + typed handle；不再为同一事实建立 Row 影子。 |
| 461 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccelerationStructureViewAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 462 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccessNormalizer</code> | 保留 | <code>AccessNormalizer</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 463 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AccessRow</code> | 合并 | <code>PassInputData / PassOutputData</code> | Unity compiler 将 read/write edge 表达为 PassInputData/PassOutputData；附件为 PassFragmentData。 |
| 464 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AliasBarrierRow</code> | 合并 | <code>BarrierGroup</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 465 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.AliasingStatistics</code> | 保留 | <code>AliasingStatistics</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 466 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ArenaColumn&lt;T&gt;</code> | 保留 | <code>ArenaColumn&lt;T&gt;</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 467 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ArenaColumn&lt;T&gt;.Chunk</code> | 保留 | <code>ArenaColumn&lt;T&gt;.Chunk</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 468 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ArenaColumn&lt;T&gt;.Enumerator</code> | 保留 | <code>ArenaColumn&lt;T&gt;.Enumerator</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 469 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ArenaSlice&lt;T&gt;</code> | 保留 | <code>ArenaSlice&lt;T&gt;</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 470 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ArenaSlice&lt;T&gt;.Enumerator</code> | 保留 | <code>ArenaSlice&lt;T&gt;.Enumerator</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 471 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BarrierRow</code> | 合并 | <code>BarrierGroup</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 472 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BarrierRowKind</code> | 删除 | <code>—</code> | BarrierRowKind 与 flat barrier 类型重复；TransitionOrigin 只需 diagnostics bit，不是领域 enum。 |
| 473 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BarrierRowPayload</code> | 合并 | <code>BarrierGroup</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 474 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BindlessAccessRow</code> | 删除 | <code>—</code> | query/bindless/shader argument 直接存于 PassData 的 typed collections；无额外 Row 类型。 |
| 475 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BottomLevelAccelerationStructureInputsAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 476 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 477 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferBindlessAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 478 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferBoundaryIndex</code> | 保留 | <code>BufferBoundaryIndex</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 479 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferImport</code> | 合并 | <code>ResourceUnversionedData</code> | import 数据并入标准 ResourceUnversionedData；Buffer/Texture 只通过 typed handle 区分。 |
| 480 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferRow</code> | 删除 | <code>—</code> | resource/view canonical storage 直接复用 Desc + typed handle；不再为同一事实建立 Row 影子。 |
| 481 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferTransitionRow</code> | 合并 | <code>BufferBarrier</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 482 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferUnorderedAccessRow</code> | 合并 | <code>BufferBarrier</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 483 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferUse</code> | 合并 | <code>AccessFlags</code> | Unity AccessFlags 已包含 Read/Write/Discard/WriteAll；不再维护 Use/PriorContents/Coverage 三套枚举。 |
| 484 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferViewAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 485 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.BufferViewRow</code> | 删除 | <code>—</code> | resource/view canonical storage 直接复用 Desc + typed handle；不再为同一事实建立 Row 影子。 |
| 486 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ColorAttachment</code> | 删除 | <code>—</code> | attachment 由 Unity 精确方法 SetRenderAttachment/SetRenderAttachmentDepth 声明，不再造参数 wrapper。 |
| 487 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ColorAttachmentRow</code> | 改名 | <code>PassFragmentData</code> | Unity RenderGraph 编译器已有精确 PassBreakReason/PassData/PassFragmentData 术语。 |
| 488 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CommandBatch</code> | 保留 | <code>CommandBatch</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 489 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CommandSubmissionCpuTimings</code> | 保留 | <code>CommandSubmissionCpuTimings</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 490 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CommandTask</code> | 删除 | <code>—</code> | CPU record task 与 command unit 是 CommandBatch 之上的重复调度层；batch 直接持 RuntimeCmd。 |
| 491 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CommandUnitKind</code> | 删除 | <code>—</code> | AMD RPS RuntimeCmd 已以 cmdId 表示运行时命令身份；不再保留第二个 CommandUnitKind 类型。 |
| 492 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CommandUnitRow</code> | 改名 | <code>RuntimeCmd</code> | AMD RPS 已有精确 RuntimeCmd；不再把存储实现写进 Row 后缀。 |
| 493 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CompilerCpuTimings</code> | 保留 | <code>CompilerCpuTimings</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 494 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.CullingStatistics</code> | 保留 | <code>CullingStatistics</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 495 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.DepthAttachmentDeclaration</code> | 删除 | <code>—</code> | attachment 由 Unity 精确方法 SetRenderAttachment/SetRenderAttachmentDepth 声明，不再造参数 wrapper。 |
| 496 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.DepthStencilAttachment</code> | 删除 | <code>—</code> | attachment 由 Unity 精确方法 SetRenderAttachment/SetRenderAttachmentDepth 声明，不再造参数 wrapper。 |
| 497 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.DepthStencilAttachmentRow</code> | 改名 | <code>PassFragmentData</code> | Unity RenderGraph 编译器已有精确 PassBreakReason/PassData/PassFragmentData 术语。 |
| 498 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.DescriptorGroupRow</code> | 合并 | <code>WriteDescriptorSet</code> | compiler descriptor/push constant rows 直接复用标准 descriptor/push-constant facts。 |
| 499 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.GraphArena</code> | 保留 | <code>GraphArena</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 500 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.GraphArena.Page</code> | 保留 | <code>GraphArena.Page</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 501 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.GraphId</code> | 合并 | <code>BufferHandle / TextureHandle / BufferViewHandle / TextureViewHandle / AccelerationStructureHandle / SamplerHandle / DescriptorHeapHandle / QueryPoolHandle</code> | GraphId 是 untyped id；其身份并入 Unity 式 typed handles，不再单独存在。 |
| 502 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.GraphIdKind</code> | 删除 | <code>—</code> | GraphIdKind 是 typed handles 之外的第二个判别真相，直接删除。 |
| 503 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.HeapRequirementRow</code> | 合并 | <code>MemoryRequirements</code> | heap requirement 与已有标准 MemoryRequirements 合一。 |
| 504 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.InitialAccelerationStructureBuildAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 505 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.InvocationCpuTimings</code> | 保留 | <code>InvocationCpuTimings</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 506 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.IPass&lt;TParameters&gt;</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 507 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.IPassParameters&lt;TSelf&gt;</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 508 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ParameterSlice&lt;T&gt;</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 509 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassCommandScope</code> | 改名 | <code>RasterGraphContext / ComputeGraphContext / UnsafeGraphContext</code> | Unity 当前回调按 pass 类型使用 RasterGraphContext/ComputeGraphContext/UnsafeGraphContext；不采用已废弃的单一 RenderGraphContext。 |
| 510 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassFlags</code> | 保留 | <code>PassFlags</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 511 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassParametersAttribute</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 512 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassPushConstantRow</code> | 合并 | <code>PushConstantRange</code> | compiler descriptor/push constant rows 直接复用标准 descriptor/push-constant facts。 |
| 513 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassRollbackMarker</code> | 保留 | <code>PassRollbackMarker</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 514 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassRow</code> | 改名 | <code>PassData</code> | Unity RenderGraph 编译器已有精确 PassBreakReason/PassData/PassFragmentData 术语。 |
| 515 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassSchedulingAffinity</code> | 删除 | <code>—</code> | 调度亲和是 compiler policy，不是 pass authoring 公共事实。 |
| 516 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PassThunk&lt;TPass, TParameters&gt;</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 517 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PriorContents</code> | 合并 | <code>AccessFlags</code> | Unity AccessFlags 已包含 Read/Write/Discard/WriteAll；不再维护 Use/PriorContents/Coverage 三套枚举。 |
| 518 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.PushConstantAttribute</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 519 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.QueryAccessRow</code> | 删除 | <code>—</code> | query/bindless/shader argument 直接存于 PassData 的 typed collections；无额外 Row 类型。 |
| 520 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.QueryAttribute</code> | 删除 | <code>—</code> | 改用 Unity 式 RenderGraphBuilder + PassData；删除自造 source-generated parameter ABI。 |
| 521 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RasterMergeBreakReason</code> | 改名 | <code>PassBreakReason</code> | Unity RenderGraph 编译器已有精确 PassBreakReason/PassData/PassFragmentData 术语。 |
| 522 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RasterScopeCompiler</code> | 保留 | <code>RasterScopeCompiler</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 523 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RasterStatistics</code> | 保留 | <code>RasterStatistics</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 524 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RayTracingAabbAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 525 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RayTracingTriangleAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 526 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ReachabilityTable</code> | 保留 | <code>ReachabilityTable</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 527 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ReferenceColumn&lt;T&gt;</code> | 保留 | <code>ReferenceColumn&lt;T&gt;</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 528 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraph</code> | 保留 | <code>RenderGraph</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 529 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraph.PassAccessHead</code> | 保留 | <code>RenderGraph.PassAccessHead</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 530 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler</code> | 保留 | <code>RenderGraphCompiler</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 531 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.AccessHistory</code> | 保留 | <code>RenderGraphCompiler.AccessHistory</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 532 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.CommandUnitTaskMetrics</code> | 删除 | <code>—</code> | CPU record task 与 command unit 是 CommandBatch 之上的重复调度层；batch 直接持 RuntimeCmd。 |
| 533 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.ContentMask</code> | 保留 | <code>RenderGraphCompiler.ContentMask</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 534 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierChain</code> | 保留 | <code>RenderGraphCompiler.PassBarrierChain</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 535 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierEntry</code> | 保留 | <code>RenderGraphCompiler.PassBarrierEntry</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 536 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierTable</code> | 保留 | <code>RenderGraphCompiler.PassBarrierTable</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 537 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.PassPredecessorTable</code> | 保留 | <code>RenderGraphCompiler.PassPredecessorTable</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 538 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.ProducerIndex</code> | 保留 | <code>RenderGraphCompiler.ProducerIndex</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 539 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.ResourceQueueHistory</code> | 保留 | <code>RenderGraphCompiler.ResourceQueueHistory</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 540 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.TextureBarrierTracker</code> | 保留 | <code>RenderGraphCompiler.TextureBarrierTracker</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 541 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.TextureCell</code> | 保留 | <code>RenderGraphCompiler.TextureCell</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 542 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.TextureCellEnumerable</code> | 保留 | <code>RenderGraphCompiler.TextureCellEnumerable</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 543 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphCompiler.TextureCellEnumerable.Enumerator</code> | 保留 | <code>RenderGraphCompiler.TextureCellEnumerable.Enumerator</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 544 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderGraphExecutionException</code> | 保留 | <code>RenderGraphExecutionException</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 545 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.RenderingExtent</code> | 改名 | <code>Extent2D</code> | Vulkan 已有精确 Extent2D 结构。 |
| 546 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ResourceAcquisitionCpuTimings</code> | 保留 | <code>ResourceAcquisitionCpuTimings</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 547 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ResourcePlacementRow</code> | 删除 | <code>—</code> | placement 只需 heap index+offset 两列；独立 Row 与 PhysicalPlacement 都删除。 |
| 548 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ShaderArgumentKind</code> | 删除 | <code>—</code> | descriptor binding 类型已经区分资源；ShaderArgumentKind 是重复判别器。 |
| 549 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.ShaderArgumentRow</code> | 删除 | <code>—</code> | query/bindless/shader argument 直接存于 PassData 的 typed collections；无额外 Row 类型。 |
| 550 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.StencilAttachmentDeclaration</code> | 删除 | <code>—</code> | attachment 由 Unity 精确方法 SetRenderAttachment/SetRenderAttachmentDepth 声明，不再造参数 wrapper。 |
| 551 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 552 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureBindlessAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 553 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureImport</code> | 合并 | <code>ResourceUnversionedData</code> | import 数据并入标准 ResourceUnversionedData；Buffer/Texture 只通过 typed handle 区分。 |
| 554 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureRow</code> | 删除 | <code>—</code> | resource/view canonical storage 直接复用 Desc + typed handle；不再为同一事实建立 Row 影子。 |
| 555 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureTransitionRow</code> | 合并 | <code>TextureBarrier</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 556 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureUnorderedAccessRow</code> | 合并 | <code>TextureBarrier</code> | RG 编译结果直接生成标准 flat barrier，删除 RowKind/Payload/variant rows。 |
| 557 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureUse</code> | 合并 | <code>AccessFlags</code> | Unity AccessFlags 已包含 Read/Write/Discard/WriteAll；不再维护 Use/PriorContents/Coverage 三套枚举。 |
| 558 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureViewAccess</code> | 删除 | <code>—</code> | access wrapper 类型退出；builder 直接接收 typed handle + Unity AccessFlags。 |
| 559 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TextureViewRow</code> | 删除 | <code>—</code> | resource/view canonical storage 直接复用 Desc + typed handle；不再为同一事实建立 Row 影子。 |
| 560 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TopLevelAccelerationStructureInputsAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 561 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler</code> | 保留 | <code>TransientPlacementCompiler</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 562 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler.PlacementCandidate</code> | 保留 | <code>TransientPlacementCompiler.PlacementCandidate</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 563 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler.PlacementResourceRow</code> | 删除 | <code>—</code> | placement compiler 的聚合 row 只是循环临时值，字段并入现有 workspace。 |
| 564 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler.ProfileKey</code> | 保留 | <code>TransientPlacementCompiler.ProfileKey</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 565 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler.ResourceOccurrenceIndex</code> | 保留 | <code>TransientPlacementCompiler.ResourceOccurrenceIndex</code> | Render Graph/编译算法中有独立职责，且名称是 Unity/RPS 或普通算法术语。 |
| 566 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransientPlacementCompiler.ResourceOccurrenceRow</code> | 删除 | <code>—</code> | ResourceOccurrenceIndex 已经是唯一索引；其 storage row 内联。 |
| 567 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.TransitionOrigin</code> | 删除 | <code>—</code> | BarrierRowKind 与 flat barrier 类型重复；TransitionOrigin 只需 diagnostics bit，不是领域 enum。 |
| 568 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.UpdateAccelerationStructureBuildAccess</code> | 删除 | <code>—</code> | RG ray-tracing access class hierarchy复制了 RHI build geometry；builder 直接声明 handles/AccessFlags。 |
| 569 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.UploadBufferData</code> | 删除 | <code>—</code> | byte[]/ReadOnlyMemory&lt;byte&gt; 在 ImportBuffer 边界直接转移或复制；不再创建一次性 owner wrapper。 |
| 570 | <code>SomeEngine.RenderGraph</code> | <code>SomeEngine.RenderGraph.WriteCoverage</code> | 合并 | <code>AccessFlags</code> | Unity AccessFlags 已包含 Read/Write/Discard/WriteAll；不再维护 Use/PriorContents/Coverage 三套枚举。 |
| 571 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphDiagnostics</code> | 保留 | <code>RenderGraphDiagnostics</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 572 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot</code> | 保留 | <code>RenderGraphSnapshot</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 573 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.AccessRow</code> | 改名 | <code>RenderGraphSnapshot.Access</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 574 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.AliasingBarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 575 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.BarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 576 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.BatchRow</code> | 改名 | <code>RenderGraphSnapshot.Batch</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 577 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.BufferTransitionBarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 578 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.BufferUnorderedAccessBarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 579 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.PassRow</code> | 改名 | <code>RenderGraphSnapshot.Pass</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 580 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.QueuePositionRow</code> | 改名 | <code>RenderGraphSnapshot.Fence</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 581 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.ResourceRow</code> | 改名 | <code>RenderGraphSnapshot.Resource</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 582 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.TaskRow</code> | 改名 | <code>RenderGraphSnapshot.Task</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 583 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.TextureTransitionBarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 584 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.TextureUnorderedAccessBarrierRow</code> | 合并 | <code>RenderGraphSnapshot.Barrier</code> | diagnostics barrier subclass 继承树合为一个 flat Barrier projection。 |
| 585 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.TimingRow</code> | 改名 | <code>RenderGraphSnapshot.Timing</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 586 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.UnitRow</code> | 改名 | <code>RenderGraphSnapshot.Command</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 587 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotDiff</code> | 保留 | <code>RenderGraphSnapshotDiff</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 588 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotDot</code> | 保留 | <code>RenderGraphSnapshotDot</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 589 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotHtml</code> | 保留 | <code>RenderGraphSnapshotHtml</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 590 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotJson</code> | 保留 | <code>RenderGraphSnapshotJson</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 591 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotQuery</code> | 保留 | <code>RenderGraphSnapshotQuery</code> | detached diagnostics projection 与 formatter/query 有独立职责。 |
| 592 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.SnapshotClockDomain</code> | 改名 | <code>ClockDomain</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 593 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.SnapshotMaterializer</code> | 删除 | <code>—</code> | materialization 是 RenderGraphDiagnostics 的实现函数，不形成额外 service 层。 |
| 594 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.SnapshotTimeUnit</code> | 改名 | <code>TimeUnit</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 595 | <code>SomeEngine.RenderGraph.Diagnostics</code> | <code>SomeEngine.RenderGraph.Diagnostics.SnapshotTransitionOrigin</code> | 改名 | <code>TransitionOrigin</code> | snapshot 中的 Row 只是在暴露存储布局；改为 detached domain record 名。 |
| 596 | <code>SomeEngine.Generators</code> | <code>SomeEngine.Generators.RenderGraphParameterGenerator</code> | 删除 | <code>—</code> | Unity 式 RenderGraphBuilder/PassData 取代自造 parameter source-generator ABI，生成器整条删除。 |
| 597 | <code>SomeEngine.Generators</code> | <code>SomeEngine.Generators.RenderGraphParameterGenerator.ParameterFieldKind</code> | 删除 | <code>—</code> | Unity 式 RenderGraphBuilder/PassData 取代自造 parameter source-generator ABI，生成器整条删除。 |
| 598 | <code>SomeEngine.Generators</code> | <code>SomeEngine.Generators.RenderGraphParameterGenerator.ParameterMember</code> | 删除 | <code>—</code> | Unity 式 RenderGraphBuilder/PassData 取代自造 parameter source-generator ABI，生成器整条删除。 |
