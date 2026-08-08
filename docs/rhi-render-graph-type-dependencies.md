# RHI / Render Graph 完整类型依赖图索引

本文件同时读取当前工作树的 Roslyn semantic source 和内存编译结果。边方向为 `source type → referenced type`；权威边集取两者并集，因此既保留会在编译时擦除的 enum/constant 等语义依赖，也保留源码没有显式写出但在签名或 IL 中出现的推断/隐式依赖。每个源/目标类型对只保留一次，同时保存 `signature`、`inheritance`、`creation`、`body-use`、`containment` 类明确关系。`value-state` 是内联值，没有独立释放生命周期；引用类型 `state` 只表示持久保存，所有权必须继续根据构造、逃逸、发布、替换与清理路径推导。rank 先压缩强连通分量，再按依赖叶子计算，仅用于依赖顺序和审计，不是人为架构层级。

- 节点：330
- 去重依赖对：1729
- 带类别边：3173
  - `body-use`：1337
  - `containment`：58
  - `creation`：402
  - `inheritance`：49
  - `signature`：1327
- 强连通分量：221
- 多节点强连通分量：8
- 最大 rank：8
- 完整数据：[`rhi-render-graph-type-dependencies.json`](rhi-render-graph-type-dependencies.json)
- Graphviz：[`rhi-render-graph-type-dependencies.dot`](rhi-render-graph-type-dependencies.dot)
- 可直接打开的完整图：[`rhi-render-graph-type-dependencies.svg`](rhi-render-graph-type-dependencies.svg)
- 重新生成：`dotnet run --project tools/RhiTypeGraph/RhiTypeGraph.csproj -- <repository-root>`
- 判断台账：[`rhi-render-graph-concept-audit.md`](rhi-render-graph-concept-audit.md)

## 边口径对账

上一版 `2,720` 来自源码 `SimpleNameSyntax` 显式名称扫描；第一轮 `2,728` 来自编译后签名和 IL。二者不是相差八条孤立边，而是两组较大的差异相抵后的净值。两者各自漏掉另一方能看到的真实依赖，因此当前权威图取并集；完整逐对差异保存在 JSON 的 `edgeMethodReconciliation` 中。

| 集合 | 类型对 |
| --- | ---: |
| 两种方法共有 | 1526 |
| 仅编译图有 | 106 |
| 仅显式语法图有 | 97 |
| 历史净差 | +9 |
| 当前并集 | 1729 |

仅编译图的类别成员数（同一类型对可属于多个类别）：

- `contains`：3
- `creates`：6
- `implements`：4
- `uses`：98

## 程序集统计

| 程序集 | 节点 | 跨程序集出边 | 跨程序集入边 |
| --- | ---: | ---: | ---: |
| `SomeEngine.Graphics` | 171 | 0 | 789 |
| `SomeEngine.Graphics.Direct3D12` | 42 | 284 | 0 |
| `SomeEngine.Graphics.Null` | 30 | 311 | 0 |
| `SomeEngine.RenderGraph` | 68 | 168 | 16 |
| `SomeEngine.RenderGraph.Diagnostics` | 19 | 42 | 0 |

## Rank 统计

| Rank | 节点 |
| ---: | ---: |
| 0 | 126 |
| 1 | 50 |
| 2 | 13 |
| 3 | 5 |
| 4 | 59 |
| 5 | 12 |
| 6 | 49 |
| 7 | 15 |
| 8 | 1 |

## 全部节点

### Rank 0

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.Format` | `SomeEngine.Graphics` | 42 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.QueueType` | `SomeEngine.Graphics` | 34 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.ResourceState` | `SomeEngine.Graphics` | 28 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.BufferRange` | `SomeEngine.Graphics` | 23 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.MemoryType` | `SomeEngine.Graphics` | 22 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.DescriptorType` | `SomeEngine.Graphics` | 19 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.ImageAspectFlags` | `SomeEngine.Graphics` | 18 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.RenderGraph.ArenaSlice<T>` | `SomeEngine.RenderGraph` | 18 | 1 | `src/SomeEngine.RenderGraph/Internal/GraphArena.cs` |
| `SomeEngine.Graphics.ImageAspect` | `SomeEngine.Graphics` | 15 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.ShaderStageFlags` | `SomeEngine.Graphics` | 15 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.AccessFlags` | `SomeEngine.Graphics` | 14 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.TextureDimension` | `SomeEngine.Graphics` | 13 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.BufferUsage` | `SomeEngine.Graphics` | 11 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.LoadAction` | `SomeEngine.Graphics` | 11 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.TextureUsage` | `SomeEngine.Graphics` | 11 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.AccelerationStructureType` | `SomeEngine.Graphics` | 10 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.BarrierFlags` | `SomeEngine.Graphics` | 10 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.HeapFlags` | `SomeEngine.Graphics` | 9 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.PipelineStatus` | `SomeEngine.Graphics` | 9 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.TextureViewDimension` | `SomeEngine.Graphics` | 9 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.IndexFormat` | `SomeEngine.Graphics` | 8 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.ResidencyPriority` | `SomeEngine.Graphics` | 8 | 0 | `src/SomeEngine.Graphics/Resources/MemoryResidency.cs` |
| `SomeEngine.Graphics.ResolveMode` | `SomeEngine.Graphics` | 8 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.TextureViewUsage` | `SomeEngine.Graphics` | 8 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.CompareOperation` | `SomeEngine.Graphics` | 7 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.PrimitiveTopology` | `SomeEngine.Graphics` | 7 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.QueryType` | `SomeEngine.Graphics` | 7 | 0 | `src/SomeEngine.Graphics/Commands/Queries.cs` |
| `SomeEngine.Graphics.ShaderStage` | `SomeEngine.Graphics` | 7 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.WorkGraphMemoryRequirements` | `SomeEngine.Graphics` | 7 | 0 | `src/SomeEngine.Graphics/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.FormatBlockLayout` | `SomeEngine.Graphics` | 6 | 0 | `src/SomeEngine.Graphics/Resources/FormatExtensions.cs` |
| `SomeEngine.Graphics.ShaderModuleIdentifier` | `SomeEngine.Graphics` | 6 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.RenderGraph.PassFlags` | `SomeEngine.RenderGraph` | 6 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.Graphics.CopyAccelerationStructureMode` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.RenderingFlags` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.SetWorkGraphFlags` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.ShadingRate` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/VariableRateShading.cs` |
| `SomeEngine.Graphics.ShadingRateCombiner` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/VariableRateShading.cs` |
| `SomeEngine.Graphics.StoreAction` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.VariableRateShadingTier` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/VariableRateShading.cs` |
| `SomeEngine.Graphics.VertexBufferLayout` | `SomeEngine.Graphics` | 5 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.RenderGraph.ArenaSlice<T>.Enumerator` | `SomeEngine.RenderGraph` | 5 | 1 | `src/SomeEngine.RenderGraph/Internal/GraphArena.cs` |
| `SomeEngine.RenderGraph.BaseRenderFunc<PassData, ContextType>` | `SomeEngine.RenderGraph` | 5 | 0 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.TextureViewHandle` | `SomeEngine.RenderGraph` | 5 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Resource` | `SomeEngine.RenderGraph.Diagnostics` | 5 | 0 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.AccelerationStructureBuildSizesInfo` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.BuildAccelerationStructureFlags` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.ColorSpace` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Device/Presentation.cs` |
| `SomeEngine.Graphics.CullMode` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.DebugMessageSeverity` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Diagnostics/GraphicsDiagnostic.cs` |
| `SomeEngine.Graphics.DeviceLimits` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Device/DeviceCapabilities.cs` |
| `SomeEngine.Graphics.FillMode` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.FrontFace` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.GeometryFlags` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.MapMode` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Resources/BufferMapping.cs` |
| `SomeEngine.Graphics.MemoryBudget` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Resources/MemoryResidency.cs` |
| `SomeEngine.Graphics.PresentMode` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Device/Presentation.cs` |
| `SomeEngine.Graphics.PresentResult` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Device/Presentation.cs` |
| `SomeEngine.Graphics.Rect2D` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.ResourceHeapTier` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Device/ResourceHeapTier.cs` |
| `SomeEngine.Graphics.SparseResourceTier` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.StencilOperation` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.StridedDeviceAddressRegion` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.TextureSampleType` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.Viewport` | `SomeEngine.Graphics` | 4 | 0 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.RenderGraph.AccelerationStructureHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.BufferHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.BufferViewHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.CompilerCpuTimings` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.DescriptorHeapHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.QueryPoolHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.TextureHandle` | `SomeEngine.RenderGraph` | 4 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.RenderGraph.Diagnostics.ClockDomain` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 0 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.RenderGraph.Diagnostics.TimeUnit` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 0 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.AddressMode` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.BorderColor` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.DispatchArguments` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Commands/IndirectCommands.cs` |
| `SomeEngine.Graphics.DrawArguments` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Commands/IndirectCommands.cs` |
| `SomeEngine.Graphics.DrawIndexedArguments` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Commands/IndirectCommands.cs` |
| `SomeEngine.Graphics.DxilLibrary` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.FilterMode` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.FormatSupport` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Device/DeviceCapabilities.cs` |
| `SomeEngine.Graphics.HitGroupType` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.MeshShaderTier` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/MeshShaders.cs` |
| `SomeEngine.Graphics.PackedMipInfo` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.PipelineCacheStatistics` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.PipelineCompilationStatistics` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.PipelineWarmupResult` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.PresentInfo` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Device/Presentation.cs` |
| `SomeEngine.Graphics.QueueFlags` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Device/DeviceCapabilities.cs` |
| `SomeEngine.Graphics.RayTracingTier` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.SamplerFeedbackTier` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/SamplerFeedback.cs` |
| `SomeEngine.Graphics.ShaderBinaryFormat` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.SubresourceTiling` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.TileRegionSize` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.TileShape` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.TiledResourceCoordinate` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.WorkGraphTier` | `SomeEngine.Graphics` | 3 | 0 | `src/SomeEngine.Graphics/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.Direct3D12.RootDescriptorTable` | `SomeEngine.Graphics.Direct3D12` | 3 | 0 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedStorage` | `SomeEngine.Graphics.Null` | 3 | 0 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.RenderGraph.CommandSubmissionCpuTimings` | `SomeEngine.RenderGraph` | 3 | 0 | `src/SomeEngine.RenderGraph/Execution/RenderGraph.Execution.cs` |
| `SomeEngine.RenderGraph.Extent2D` | `SomeEngine.RenderGraph` | 3 | 0 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.ResourceAcquisitionCpuTimings` | `SomeEngine.RenderGraph` | 3 | 0 | `src/SomeEngine.RenderGraph/Execution/RenderGraph.Execution.cs` |
| `SomeEngine.RenderGraph.SamplerHandle` | `SomeEngine.RenderGraph` | 3 | 0 | `src/SomeEngine.RenderGraph/Authoring/ResourceHandles.cs` |
| `SomeEngine.Graphics.BlendFactor` | `SomeEngine.Graphics` | 2 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.BlendOperation` | `SomeEngine.Graphics` | 2 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.ColorWriteMask` | `SomeEngine.Graphics` | 2 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.PipelineLayout.DescriptorRangeType` | `SomeEngine.Graphics` | 2 | 0 | `src/SomeEngine.Graphics/Pipelines/PipelineLayout.Registers.cs` |
| `SomeEngine.Graphics.Direct3D12.CpuDescriptorPool.Page` | `SomeEngine.Graphics.Direct3D12` | 2 | 0 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/CpuDescriptorPool.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.QueryCounters` | `SomeEngine.Graphics.Null` | 2 | 0 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.Graphics.Null.NullDeviceStatistics` | `SomeEngine.Graphics.Null` | 2 | 0 | `src/SomeEngine.Graphics.Null/DeviceConfiguration.cs` |
| `SomeEngine.RenderGraph.AliasingStatistics` | `SomeEngine.RenderGraph` | 2 | 0 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.CullingStatistics` | `SomeEngine.RenderGraph` | 2 | 0 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.PassRollbackMarker` | `SomeEngine.RenderGraph` | 2 | 0 | `src/SomeEngine.RenderGraph/Authoring/RenderGraph.Authoring.cs` |
| `SomeEngine.RenderGraph.ReferenceColumn<T>` | `SomeEngine.RenderGraph` | 2 | 0 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierChain` | `SomeEngine.RenderGraph` | 2 | 0 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.Diagnostics.TransitionOrigin` | `SomeEngine.RenderGraph.Diagnostics` | 2 | 0 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.PipelineStatistics` | `SomeEngine.Graphics` | 1 | 0 | `src/SomeEngine.Graphics/Commands/Queries.cs` |
| `SomeEngine.Graphics.ShaderRecord` | `SomeEngine.Graphics` | 1 | 0 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Device.IndirectSignatureKey` | `SomeEngine.Graphics.Direct3D12` | 1 | 0 | `src/SomeEngine.Graphics.Direct3D12/Device/Device.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Device.PipelineStateStream` | `SomeEngine.Graphics.Direct3D12` | 1 | 0 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/MeshShaderSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.RangeAllocator.FreeRange` | `SomeEngine.Graphics.Direct3D12` | 1 | 0 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/ShaderVisibleDescriptorPool.cs` |
| `SomeEngine.RenderGraph.ArenaColumn<T>.Chunk` | `SomeEngine.RenderGraph` | 1 | 0 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.RenderGraph.GraphArena.Page` | `SomeEngine.RenderGraph` | 1 | 0 | `src/SomeEngine.RenderGraph/Internal/GraphArena.cs` |
| `SomeEngine.RenderGraph.PassBreakReason` | `SomeEngine.RenderGraph` | 1 | 0 | `src/SomeEngine.RenderGraph/Compilation/RasterScopeCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraph.PassAccessHead` | `SomeEngine.RenderGraph` | 1 | 0 | `src/SomeEngine.RenderGraph/Authoring/RenderGraph.Authoring.cs` |
| `SomeEngine.RenderGraph.TransientPlacementCompiler.PlacementCandidate` | `SomeEngine.RenderGraph` | 1 | 0 | `src/SomeEngine.RenderGraph/Compilation/TransientPlacementCompiler.cs` |

### Rank 1

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.FormatExtensions` | `SomeEngine.Graphics` | 15 | 4 | `src/SomeEngine.Graphics/Resources/FormatExtensions.cs` |
| `SomeEngine.Graphics.BufferDesc` | `SomeEngine.Graphics` | 14 | 1 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.DescriptorSetLayoutBinding` | `SomeEngine.Graphics` | 12 | 6 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.BufferImageCopy` | `SomeEngine.Graphics` | 11 | 1 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.PushConstantRange` | `SomeEngine.Graphics` | 11 | 1 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.RenderGraph.PassData` | `SomeEngine.RenderGraph` | 9 | 2 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.Graphics.HeapDesc` | `SomeEngine.Graphics` | 8 | 2 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.MemoryRequirements` | `SomeEngine.Graphics` | 8 | 2 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.BlendAttachment` | `SomeEngine.Graphics` | 6 | 3 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.DescriptorHeapDesc` | `SomeEngine.Graphics` | 6 | 1 | `src/SomeEngine.Graphics/Resources/Bindless.cs` |
| `SomeEngine.Graphics.QueryPoolCreateInfo` | `SomeEngine.Graphics` | 6 | 2 | `src/SomeEngine.Graphics/Commands/Queries.cs` |
| `SomeEngine.Graphics.RasterizerState` | `SomeEngine.Graphics` | 6 | 3 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.SamplerDesc` | `SomeEngine.Graphics` | 6 | 4 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.SwapchainCreateInfo` | `SomeEngine.Graphics` | 6 | 3 | `src/SomeEngine.Graphics/Device/Presentation.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Pass` | `SomeEngine.RenderGraph.Diagnostics` | 6 | 2 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.TileMapping` | `SomeEngine.Graphics` | 5 | 2 | `src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.VertexAttribute` | `SomeEngine.Graphics` | 5 | 1 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.RenderGraph.AliasingBarrier` | `SomeEngine.RenderGraph` | 5 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Access` | `SomeEngine.RenderGraph.Diagnostics` | 5 | 3 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.ClockCalibration` | `SomeEngine.Graphics` | 4 | 1 | `src/SomeEngine.Graphics/Commands/Queries.cs` |
| `SomeEngine.Graphics.DebugMessage` | `SomeEngine.Graphics` | 4 | 1 | `src/SomeEngine.Graphics/Diagnostics/GraphicsDiagnostic.cs` |
| `SomeEngine.Graphics.StencilFace` | `SomeEngine.Graphics` | 4 | 2 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.RenderGraph.ResourceUnversionedData` | `SomeEngine.RenderGraph` | 4 | 2 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Fence` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 1 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Timing` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 2 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.BufferMapping` | `SomeEngine.Graphics` | 3 | 2 | `src/SomeEngine.Graphics/Resources/BufferMapping.cs` |
| `SomeEngine.Graphics.HitGroupDesc` | `SomeEngine.Graphics` | 3 | 1 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.Direct3D12.RootConstants` | `SomeEngine.Graphics.Direct3D12` | 3 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Null.TexturePlaneEnumerator` | `SomeEngine.Graphics.Null` | 3 | 2 | `src/SomeEngine.Graphics.Null/Resources/TextureLayout.cs` |
| `SomeEngine.RenderGraph.CommandBatch` | `SomeEngine.RenderGraph` | 3 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.PassFragmentData` | `SomeEngine.RenderGraph` | 3 | 2 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.RenderGraph.RasterStatistics` | `SomeEngine.RenderGraph` | 3 | 1 | `src/SomeEngine.RenderGraph/Compilation/RasterScopeCompiler.cs` |
| `SomeEngine.RenderGraph.RuntimeCmd` | `SomeEngine.RenderGraph` | 3 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Command` | `SomeEngine.RenderGraph.Diagnostics` | 3 | 1 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Task` | `SomeEngine.RenderGraph.Diagnostics` | 3 | 1 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.Direct3D12.DescriptorRange` | `SomeEngine.Graphics.Direct3D12` | 2 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.DxilProgramHeader` | `SomeEngine.Graphics.Direct3D12` | 2 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.RangeAllocator` | `SomeEngine.Graphics.Direct3D12` | 2 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/ShaderVisibleDescriptorPool.cs` |
| `SomeEngine.RenderGraph.BufferBoundaryIndex` | `SomeEngine.RenderGraph` | 2 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphLiveness.cs` |
| `SomeEngine.RenderGraph.GraphArena` | `SomeEngine.RenderGraph` | 2 | 2 | `src/SomeEngine.RenderGraph/Internal/GraphArena.cs` |
| `SomeEngine.RenderGraph.InvocationCpuTimings` | `SomeEngine.RenderGraph` | 2 | 3 | `src/SomeEngine.RenderGraph/Execution/RenderGraph.Execution.cs` |
| `SomeEngine.Graphics.PipelineLayout.DescriptorRange` | `SomeEngine.Graphics` | 1 | 2 | `src/SomeEngine.Graphics/Pipelines/PipelineLayout.Registers.cs` |
| `SomeEngine.Graphics.ShaderBindingTable` | `SomeEngine.Graphics` | 1 | 2 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.Direct3D12.CpuDescriptorPool.Bucket` | `SomeEngine.Graphics.Direct3D12` | 1 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/CpuDescriptorPool.cs` |
| `SomeEngine.Graphics.Direct3D12.DeviceConfiguration` | `SomeEngine.Graphics.Direct3D12` | 1 | 2 | `src/SomeEngine.Graphics.Direct3D12/Device/DeviceConfiguration.cs` |
| `SomeEngine.Graphics.Null.DeviceConfiguration` | `SomeEngine.Graphics.Null` | 1 | 4 | `src/SomeEngine.Graphics.Null/DeviceConfiguration.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.AccessHistory` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.ContentMask` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphLiveness.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.ProducerIndex` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphLiveness.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.ResourceQueueHistory` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |

### Rank 2

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.TextureDesc` | `SomeEngine.Graphics` | 23 | 4 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.DepthStencilState` | `SomeEngine.Graphics` | 10 | 2 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.RenderGraph.ArenaColumn<T>` | `SomeEngine.RenderGraph` | 10 | 4 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.Graphics.Direct3D12.DescriptorAllocation` | `SomeEngine.Graphics.Direct3D12` | 8 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/CpuDescriptorPool.cs` |
| `SomeEngine.Graphics.ImageCopy` | `SomeEngine.Graphics` | 6 | 1 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.PlacedSubresourceFootprint` | `SomeEngine.Graphics` | 4 | 1 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.ShaderReflection` | `SomeEngine.Graphics` | 4 | 5 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Batch` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 2 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool.DescriptorAllocation` | `SomeEngine.Graphics.Direct3D12` | 3 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/ShaderVisibleDescriptorPool.cs` |
| `SomeEngine.RenderGraph.ArenaColumn<T>.Enumerator` | `SomeEngine.RenderGraph` | 3 | 1 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.Graphics.Direct3D12.CpuDescriptorPool` | `SomeEngine.Graphics.Direct3D12` | 2 | 3 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/CpuDescriptorPool.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12InfoQueue` | `SomeEngine.Graphics.Direct3D12` | 1 | 2 | `src/SomeEngine.Graphics.Direct3D12/Native/NativeDiagnosticDrain.cs` |
| `SomeEngine.RenderGraph.TransientPlacementCompiler.ProfileKey` | `SomeEngine.RenderGraph` | 1 | 3 | `src/SomeEngine.RenderGraph/Compilation/TransientPlacementCompiler.cs` |

### Rank 3

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.SubresourceRange` | `SomeEngine.Graphics` | 29 | 4 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs`<br>`src/SomeEngine.Graphics/Resources/TextureSubresourceRange.cs` |
| `SomeEngine.Graphics.ShaderModule` | `SomeEngine.Graphics` | 14 | 12 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.DeviceCapabilities` | `SomeEngine.Graphics` | 9 | 16 | `src/SomeEngine.Graphics/Device/DeviceCapabilities.cs` |
| `SomeEngine.Graphics.ImageResolve` | `SomeEngine.Graphics` | 7 | 6 | `src/SomeEngine.Graphics/Commands/CommandList.cs`<br>`src/SomeEngine.Graphics/Commands/ImageResolve.cs` |
| `SomeEngine.Graphics.Direct3D12.ShaderVisibleDescriptorPool` | `SomeEngine.Graphics.Direct3D12` | 2 | 2 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/ShaderVisibleDescriptorPool.cs` |

### Rank 4

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.Device` | `SomeEngine.Graphics` | 26 | 65 | `src/SomeEngine.Graphics/Device/Device.cs` |
| `SomeEngine.Graphics.Buffer` | `SomeEngine.Graphics` | 25 | 7 | `src/SomeEngine.Graphics/Commands/Buffer.Synchronization.cs`<br>`src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.Texture` | `SomeEngine.Graphics` | 23 | 17 | `src/SomeEngine.Graphics/Resources/Handles.cs`<br>`src/SomeEngine.Graphics/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.PipelineLayout` | `SomeEngine.Graphics` | 20 | 14 | `src/SomeEngine.Graphics/Pipelines/PipelineLayout.Admission.cs`<br>`src/SomeEngine.Graphics/Pipelines/PipelineLayout.Registers.cs`<br>`src/SomeEngine.Graphics/Pipelines/PipelineLayout.Shaders.cs`<br>`src/SomeEngine.Graphics/Pipelines/ShaderPipelineLayout.cs`<br>`src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.GraphicsFence` | `SomeEngine.Graphics` | 19 | 2 | `src/SomeEngine.Graphics/Synchronization/QueuePosition.cs` |
| `SomeEngine.Graphics.Pipeline` | `SomeEngine.Graphics` | 17 | 22 | `src/SomeEngine.Graphics/Pipelines/ShaderPipelines.cs`<br>`src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.Heap` | `SomeEngine.Graphics` | 16 | 4 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.Resource` | `SomeEngine.Graphics` | 16 | 2 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.DescriptorSetLayout` | `SomeEngine.Graphics` | 13 | 2 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.TextureView` | `SomeEngine.Graphics` | 13 | 7 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.AccelerationStructure` | `SomeEngine.Graphics` | 12 | 3 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.BufferView` | `SomeEngine.Graphics` | 11 | 6 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.QueryPool` | `SomeEngine.Graphics` | 11 | 3 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.PipelineCacheKey` | `SomeEngine.Graphics` | 10 | 19 | `src/SomeEngine.Graphics/Pipelines/PipelineCache.cs`<br>`src/SomeEngine.Graphics/Pipelines/PipelineCacheKey.Canonical.cs` |
| `SomeEngine.Graphics.AccelerationStructureBuildGeometryInfo` | `SomeEngine.Graphics` | 9 | 7 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.CommandList` | `SomeEngine.Graphics` | 9 | 28 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.DescriptorSet` | `SomeEngine.Graphics` | 9 | 1 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.WorkGraph` | `SomeEngine.Graphics` | 9 | 3 | `src/SomeEngine.Graphics/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.BufferViewDesc` | `SomeEngine.Graphics` | 8 | 4 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs` |
| `SomeEngine.Graphics.Sampler` | `SomeEngine.Graphics` | 8 | 6 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.TextureViewDesc` | `SomeEngine.Graphics` | 8 | 10 | `src/SomeEngine.Graphics/Resources/ResourceDescriptors.cs`<br>`src/SomeEngine.Graphics/Resources/TextureViewDesc.cs` |
| `SomeEngine.Graphics.BufferBarrier` | `SomeEngine.Graphics` | 7 | 3 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.DescriptorHeap` | `SomeEngine.Graphics` | 7 | 3 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.RenderingInfo` | `SomeEngine.Graphics` | 7 | 5 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.TextureBarrier` | `SomeEngine.Graphics` | 7 | 4 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.RenderGraph.PassInputData` | `SomeEngine.RenderGraph` | 7 | 4 | `src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.Graphics.AccelerationStructureGeometryAabbsData` | `SomeEngine.Graphics` | 6 | 2 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.AccelerationStructureGeometryTrianglesData` | `SomeEngine.Graphics` | 6 | 4 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.BarrierGroup` | `SomeEngine.Graphics` | 6 | 9 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.DescriptorHandle` | `SomeEngine.Graphics` | 6 | 2 | `src/SomeEngine.Graphics/Resources/Bindless.cs` |
| `SomeEngine.RenderGraph.ResourceBarrier` | `SomeEngine.RenderGraph` | 6 | 4 | `src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs` |
| `SomeEngine.Graphics.DispatchRaysDesc` | `SomeEngine.Graphics` | 5 | 3 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.Queue` | `SomeEngine.Graphics` | 5 | 8 | `src/SomeEngine.Graphics/Synchronization/Queue.cs` |
| `SomeEngine.Graphics.RasterPipelineDesc` | `SomeEngine.Graphics` | 5 | 10 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.RenderingAttachmentInfo` | `SomeEngine.Graphics` | 5 | 4 | `src/SomeEngine.Graphics/Commands/CommandList.cs` |
| `SomeEngine.Graphics.Swapchain` | `SomeEngine.Graphics` | 5 | 5 | `src/SomeEngine.Graphics/Resources/Handles.cs` |
| `SomeEngine.Graphics.WriteDescriptorSet` | `SomeEngine.Graphics` | 5 | 4 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.ComputePipelineDesc` | `SomeEngine.Graphics` | 4 | 3 | `src/SomeEngine.Graphics/Pipelines/PipelineDescriptors.cs` |
| `SomeEngine.Graphics.MeshPipelineDesc` | `SomeEngine.Graphics` | 4 | 6 | `src/SomeEngine.Graphics/Pipelines/MeshShaders.cs` |
| `SomeEngine.Graphics.PipelineLayoutCreateInfo` | `SomeEngine.Graphics` | 4 | 2 | `src/SomeEngine.Graphics/Pipelines/ShaderArtifacts.cs` |
| `SomeEngine.Graphics.RaytracingAccelerationStructurePostbuildInfoDesc` | `SomeEngine.Graphics` | 4 | 2 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator` | `SomeEngine.Graphics` | 4 | 33 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.Direct3D12.ShaderBytecode` | `SomeEngine.Graphics.Direct3D12` | 4 | 5 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Null.TextureLayout` | `SomeEngine.Graphics.Null` | 4 | 11 | `src/SomeEngine.Graphics.Null/Resources/TextureLayout.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot.Barrier` | `SomeEngine.RenderGraph.Diagnostics` | 4 | 4 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs` |
| `SomeEngine.Graphics.RayTracingPipelineCreateInfo` | `SomeEngine.Graphics` | 3 | 4 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.SubobjectToExportsAssociation` | `SomeEngine.Graphics` | 3 | 1 | `src/SomeEngine.Graphics/Pipelines/RayTracing.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.HeapEntry` | `SomeEngine.Graphics` | 3 | 4 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.WorkGraphDesc` | `SomeEngine.Graphics` | 3 | 1 | `src/SomeEngine.Graphics/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.BufferKey` | `SomeEngine.Graphics` | 2 | 2 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.BufferViewKey` | `SomeEngine.Graphics` | 2 | 4 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.TextureKey` | `SomeEngine.Graphics` | 2 | 5 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.TextureViewKey` | `SomeEngine.Graphics` | 2 | 5 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.Null.TextureSubresourceEnumerator` | `SomeEngine.Graphics.Null` | 2 | 6 | `src/SomeEngine.Graphics.Null/Resources/TextureLayout.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.TextureCell` | `SomeEngine.RenderGraph` | 2 | 4 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.BufferEntry` | `SomeEngine.Graphics` | 1 | 5 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.BufferViewEntry` | `SomeEngine.Graphics` | 1 | 3 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.TextureEntry` | `SomeEngine.Graphics` | 1 | 5 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |
| `SomeEngine.Graphics.TransientResourceAllocator.TextureViewEntry` | `SomeEngine.Graphics` | 1 | 3 | `src/SomeEngine.Graphics/Resources/TransientResources.cs` |

### Rank 5

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshot` | `SomeEngine.RenderGraph.Diagnostics` | 6 | 11 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.cs`<br>`src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.Validation.cs` |
| `SomeEngine.RenderGraph.IBaseRenderGraphBuilder` | `SomeEngine.RenderGraph` | 5 | 13 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.Graphics.Null.NullAccelerationStructure` | `SomeEngine.Graphics.Null` | 2 | 8 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullDescriptorHeap.Entry` | `SomeEngine.Graphics.Null` | 2 | 1 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierEntry` | `SomeEngine.RenderGraph` | 2 | 1 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.TextureCellEnumerable.Enumerator` | `SomeEngine.RenderGraph` | 2 | 3 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12DescriptorHeap.Entry` | `SomeEngine.Graphics.Direct3D12` | 1 | 1 | `src/SomeEngine.Graphics.Direct3D12/Device/Bindless.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Device.PipelineCacheEntry` | `SomeEngine.Graphics.Direct3D12` | 1 | 2 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Device.PipelineCacheEntryKey` | `SomeEngine.Graphics.Direct3D12` | 1 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineCache.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12PipelineLibrary` | `SomeEngine.Graphics.Direct3D12` | 1 | 1 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/NativePipelineLibrary.cs` |
| `SomeEngine.RenderGraph.AccessNormalizer` | `SomeEngine.RenderGraph` | 1 | 10 | `src/SomeEngine.RenderGraph/Compilation/AccessNormalizer.cs` |
| `SomeEngine.RenderGraph.RenderGraphExecutionException` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Execution/RenderGraphExecutionException.cs` |

### Rank 6

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.Graphics.Direct3D12.D3D12Device` | `SomeEngine.Graphics.Direct3D12` | 19 | 169 | `src/SomeEngine.Graphics.Direct3D12/Commands/Queries.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/VariableRateShading.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Bindless.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Capabilities.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Device.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Device.Objects.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Device.Queues.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Device.Releases.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/HostAccess.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/ObjectNaming.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Device/Presentation.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Native/DeviceInitialization.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Native/DredDiagnostics.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Native/NativeConversions.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/MeshShaderSurface.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineCache.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineLayouts.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/RayTracingSurface.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/SamplerFeedback.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Pipelines/WorkGraphs.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Resources/ExplicitCommands.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Resources/Resources.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Resources/SparseResources.cs` |
| `SomeEngine.Graphics.Null.NullDevice` | `SomeEngine.Graphics.Null` | 17 | 156 | `src/SomeEngine.Graphics.Null/Device/Device.AdvancedCommands.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.AdvancedPipelines.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Bindless.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Capabilities.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Execution.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.HostAccess.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Objects.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Pipelines.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Presentation.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Queues.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Releases.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Resources.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.SparseResources.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.Submission.cs`<br>`src/SomeEngine.Graphics.Null/Device/Device.WorkGraphs.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12PipelineLayout` | `SomeEngine.Graphics.Direct3D12` | 7 | 6 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Buffer` | `SomeEngine.Graphics.Direct3D12` | 5 | 9 | `src/SomeEngine.Graphics.Direct3D12/Resources/Resources.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12BufferView` | `SomeEngine.Graphics.Direct3D12` | 4 | 6 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSetLayout` | `SomeEngine.Graphics.Direct3D12` | 4 | 5 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Heap` | `SomeEngine.Graphics.Direct3D12` | 4 | 3 | `src/SomeEngine.Graphics.Direct3D12/Resources/Resources.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Sampler` | `SomeEngine.Graphics.Direct3D12` | 4 | 4 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12TextureView` | `SomeEngine.Graphics.Direct3D12` | 4 | 12 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSet.Entry` | `SomeEngine.Graphics.Direct3D12` | 3 | 6 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Texture` | `SomeEngine.Graphics.Direct3D12` | 3 | 10 | `src/SomeEngine.Graphics.Direct3D12/Resources/Resources.cs` |
| `SomeEngine.Graphics.Null.CommandStream` | `SomeEngine.Graphics.Null` | 3 | 2 | `src/SomeEngine.Graphics.Null/Commands/CommandStream.cs` |
| `SomeEngine.Graphics.Null.NullBuffer` | `SomeEngine.Graphics.Null` | 3 | 10 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction` | `SomeEngine.Graphics.Null` | 3 | 57 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.Graphics.Null.NullTexture` | `SomeEngine.Graphics.Null` | 3 | 11 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.RenderGraph.IRenderAttachmentRenderGraphBuilder` | `SomeEngine.RenderGraph` | 3 | 5 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12ComputePipeline` | `SomeEngine.Graphics.Direct3D12` | 2 | 6 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSet` | `SomeEngine.Graphics.Direct3D12` | 2 | 4 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/BindingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12MeshPipeline` | `SomeEngine.Graphics.Direct3D12` | 2 | 8 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/MeshShaderSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12QueryPool` | `SomeEngine.Graphics.Direct3D12` | 2 | 3 | `src/SomeEngine.Graphics.Direct3D12/Commands/Queries.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12RasterPipeline` | `SomeEngine.Graphics.Direct3D12` | 2 | 9 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/PipelineSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12RayTracingPipeline` | `SomeEngine.Graphics.Direct3D12` | 2 | 5 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/RayTracingSurface.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12WorkGraph` | `SomeEngine.Graphics.Direct3D12` | 2 | 4 | `src/SomeEngine.Graphics.Direct3D12/Pipelines/WorkGraphs.cs` |
| `SomeEngine.Graphics.Null.NullDescriptorSet` | `SomeEngine.Graphics.Null` | 2 | 4 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullPipeline` | `SomeEngine.Graphics.Null` | 2 | 9 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullQueryPool` | `SomeEngine.Graphics.Null` | 2 | 3 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullTextureView` | `SomeEngine.Graphics.Null` | 2 | 4 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12CommandList` | `SomeEngine.Graphics.Direct3D12` | 1 | 89 | `src/SomeEngine.Graphics.Direct3D12/Commands/CommandRecorder.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/DescriptorHeaps.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/EnhancedBarriers.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/MeshShaderCommands.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/RayTracingCommands.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/SamplerFeedback.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/VariableRateShading.cs`<br>`src/SomeEngine.Graphics.Direct3D12/Commands/WorkGraphs.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12DescriptorHeap` | `SomeEngine.Graphics.Direct3D12` | 1 | 12 | `src/SomeEngine.Graphics.Direct3D12/Device/Bindless.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Queue` | `SomeEngine.Graphics.Direct3D12` | 1 | 2 | `src/SomeEngine.Graphics.Direct3D12/Native/DeviceInitialization.cs` |
| `SomeEngine.Graphics.Direct3D12.D3D12Swapchain` | `SomeEngine.Graphics.Direct3D12` | 1 | 4 | `src/SomeEngine.Graphics.Direct3D12/Device/Presentation.cs` |
| `SomeEngine.Graphics.Null.NullBufferView` | `SomeEngine.Graphics.Null` | 1 | 3 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullCommandList` | `SomeEngine.Graphics.Null` | 1 | 49 | `src/SomeEngine.Graphics.Null/Commands/CommandRecorder.cs`<br>`src/SomeEngine.Graphics.Null/Commands/CommandRecorder.Queries.cs`<br>`src/SomeEngine.Graphics.Null/Commands/RayTracingBindings.cs` |
| `SomeEngine.Graphics.Null.NullDescriptorHeap` | `SomeEngine.Graphics.Null` | 1 | 4 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullDescriptorSetLayout` | `SomeEngine.Graphics.Null` | 1 | 3 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedBuffer` | `SomeEngine.Graphics.Null` | 1 | 7 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedQueryPool` | `SomeEngine.Graphics.Null` | 1 | 2 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedTexture` | `SomeEngine.Graphics.Null` | 1 | 6 | `src/SomeEngine.Graphics.Null/Device/Device.Submission.cs` |
| `SomeEngine.Graphics.Null.NullHeap` | `SomeEngine.Graphics.Null` | 1 | 3 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullPipelineLayout` | `SomeEngine.Graphics.Null` | 1 | 4 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullSampler` | `SomeEngine.Graphics.Null` | 1 | 3 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullSwapchain` | `SomeEngine.Graphics.Null` | 1 | 4 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.Graphics.Null.NullWorkGraph` | `SomeEngine.Graphics.Null` | 1 | 5 | `src/SomeEngine.Graphics.Null/Device/Records.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.TextureCellEnumerable` | `SomeEngine.RenderGraph` | 1 | 2 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotDiff` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 10 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshotDiff.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotDot` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 6 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshotDot.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotHtml` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 9 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshotHtml.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotJson` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 1 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshotJson.cs` |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphSnapshotQuery` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 4 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshotQuery.cs` |

### Rank 7

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.RenderGraph.RenderGraph` | `SomeEngine.RenderGraph` | 11 | 97 | `src/SomeEngine.RenderGraph/Authoring/RenderGraph.Api.cs`<br>`src/SomeEngine.RenderGraph/Authoring/RenderGraph.Authoring.cs`<br>`src/SomeEngine.RenderGraph/Authoring/RenderGraph.ShaderParameters.cs`<br>`src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs`<br>`src/SomeEngine.RenderGraph/Compilation/PassCompilationStorage.cs`<br>`src/SomeEngine.RenderGraph/Compilation/RenderGraph.Compilation.cs`<br>`src/SomeEngine.RenderGraph/Execution/RenderGraph.cs`<br>`src/SomeEngine.RenderGraph/Execution/RenderGraph.Execution.cs`<br>`src/SomeEngine.RenderGraph/Internal/RenderGraph.Storage.cs` |
| `SomeEngine.RenderGraph.UnsafeGraphContext` | `SomeEngine.RenderGraph` | 6 | 38 | `src/SomeEngine.RenderGraph/Execution/UnsafeGraphContext.cs` |
| `SomeEngine.RenderGraph.ReachabilityTable` | `SomeEngine.RenderGraph` | 4 | 5 | `src/SomeEngine.RenderGraph/Compilation/ReachabilityTable.cs` |
| `SomeEngine.RenderGraph.IComputeRenderGraphBuilder` | `SomeEngine.RenderGraph` | 2 | 3 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.IRasterRenderGraphBuilder` | `SomeEngine.RenderGraph` | 2 | 4 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.IUnsafeRenderGraphBuilder` | `SomeEngine.RenderGraph` | 2 | 4 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.PassExecutor` | `SomeEngine.RenderGraph` | 1 | 1 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.RasterScopeCompiler` | `SomeEngine.RenderGraph` | 1 | 17 | `src/SomeEngine.RenderGraph/Compilation/RasterScopeCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraphBuilders` | `SomeEngine.RenderGraph` | 1 | 24 | `src/SomeEngine.RenderGraph/Authoring/RenderGraphBuilders.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler` | `SomeEngine.RenderGraph` | 1 | 59 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs`<br>`src/SomeEngine.RenderGraph/Compilation/RenderGraphLiveness.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierTable` | `SomeEngine.RenderGraph` | 1 | 8 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.PassPredecessorTable` | `SomeEngine.RenderGraph` | 1 | 4 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.RenderGraphCompiler.TextureBarrierTracker` | `SomeEngine.RenderGraph` | 1 | 6 | `src/SomeEngine.RenderGraph/Compilation/RenderGraphCompiler.cs` |
| `SomeEngine.RenderGraph.TransientPlacementCompiler` | `SomeEngine.RenderGraph` | 1 | 16 | `src/SomeEngine.RenderGraph/Compilation/TransientPlacementCompiler.cs` |
| `SomeEngine.RenderGraph.TransientPlacementCompiler.ResourceOccurrenceIndex` | `SomeEngine.RenderGraph` | 1 | 3 | `src/SomeEngine.RenderGraph/Compilation/TransientPlacementCompiler.cs` |

### Rank 8

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.RenderGraph.Diagnostics.RenderGraphDiagnostics` | `SomeEngine.RenderGraph.Diagnostics` | 0 | 39 | `src/SomeEngine.RenderGraph.Diagnostics/RenderGraphDiagnostics.cs` |

## 多节点强连通分量

- SCC 2: `SomeEngine.Graphics.Direct3D12.CpuDescriptorPool`, `SomeEngine.Graphics.Direct3D12.DescriptorAllocation`
- SCC 117: `SomeEngine.Graphics.AccelerationStructure`, `SomeEngine.Graphics.AccelerationStructureBuildGeometryInfo`, `SomeEngine.Graphics.AccelerationStructureGeometryAabbsData`, `SomeEngine.Graphics.AccelerationStructureGeometryTrianglesData`, `SomeEngine.Graphics.BarrierGroup`, `SomeEngine.Graphics.Buffer`, `SomeEngine.Graphics.BufferBarrier`, `SomeEngine.Graphics.BufferView`, `SomeEngine.Graphics.BufferViewDesc`, `SomeEngine.Graphics.CommandList`, `SomeEngine.Graphics.ComputePipelineDesc`, `SomeEngine.Graphics.DescriptorHandle`, `SomeEngine.Graphics.DescriptorHeap`, `SomeEngine.Graphics.DescriptorSet`, `SomeEngine.Graphics.DescriptorSetLayout`, `SomeEngine.Graphics.Device`, `SomeEngine.Graphics.DispatchRaysDesc`, `SomeEngine.Graphics.GraphicsFence`, `SomeEngine.Graphics.Heap`, `SomeEngine.Graphics.MeshPipelineDesc`, `SomeEngine.Graphics.Pipeline`, `SomeEngine.Graphics.PipelineCacheKey`, `SomeEngine.Graphics.PipelineLayout`, `SomeEngine.Graphics.PipelineLayoutCreateInfo`, `SomeEngine.Graphics.QueryPool`, `SomeEngine.Graphics.Queue`, `SomeEngine.Graphics.RasterPipelineDesc`, `SomeEngine.Graphics.RayTracingPipelineCreateInfo`, `SomeEngine.Graphics.RaytracingAccelerationStructurePostbuildInfoDesc`, `SomeEngine.Graphics.RenderingAttachmentInfo`, `SomeEngine.Graphics.RenderingInfo`, `SomeEngine.Graphics.Resource`, `SomeEngine.Graphics.Sampler`, `SomeEngine.Graphics.SubobjectToExportsAssociation`, `SomeEngine.Graphics.Swapchain`, `SomeEngine.Graphics.Texture`, `SomeEngine.Graphics.TextureBarrier`, `SomeEngine.Graphics.TextureView`, `SomeEngine.Graphics.TextureViewDesc`, `SomeEngine.Graphics.TransientResourceAllocator`, `SomeEngine.Graphics.TransientResourceAllocator.BufferEntry`, `SomeEngine.Graphics.TransientResourceAllocator.BufferKey`, `SomeEngine.Graphics.TransientResourceAllocator.BufferViewEntry`, `SomeEngine.Graphics.TransientResourceAllocator.BufferViewKey`, `SomeEngine.Graphics.TransientResourceAllocator.HeapEntry`, `SomeEngine.Graphics.TransientResourceAllocator.TextureEntry`, `SomeEngine.Graphics.TransientResourceAllocator.TextureKey`, `SomeEngine.Graphics.TransientResourceAllocator.TextureViewEntry`, `SomeEngine.Graphics.TransientResourceAllocator.TextureViewKey`, `SomeEngine.Graphics.WorkGraph`, `SomeEngine.Graphics.WorkGraphDesc`, `SomeEngine.Graphics.WriteDescriptorSet`
- SCC 140: `SomeEngine.Graphics.Direct3D12.D3D12Buffer`, `SomeEngine.Graphics.Direct3D12.D3D12BufferView`, `SomeEngine.Graphics.Direct3D12.D3D12CommandList`, `SomeEngine.Graphics.Direct3D12.D3D12ComputePipeline`, `SomeEngine.Graphics.Direct3D12.D3D12DescriptorHeap`, `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSet`, `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSet.Entry`, `SomeEngine.Graphics.Direct3D12.D3D12DescriptorSetLayout`, `SomeEngine.Graphics.Direct3D12.D3D12Device`, `SomeEngine.Graphics.Direct3D12.D3D12Heap`, `SomeEngine.Graphics.Direct3D12.D3D12MeshPipeline`, `SomeEngine.Graphics.Direct3D12.D3D12PipelineLayout`, `SomeEngine.Graphics.Direct3D12.D3D12QueryPool`, `SomeEngine.Graphics.Direct3D12.D3D12Queue`, `SomeEngine.Graphics.Direct3D12.D3D12RasterPipeline`, `SomeEngine.Graphics.Direct3D12.D3D12RayTracingPipeline`, `SomeEngine.Graphics.Direct3D12.D3D12Sampler`, `SomeEngine.Graphics.Direct3D12.D3D12Swapchain`, `SomeEngine.Graphics.Direct3D12.D3D12Texture`, `SomeEngine.Graphics.Direct3D12.D3D12TextureView`, `SomeEngine.Graphics.Direct3D12.D3D12WorkGraph`
- SCC 144: `SomeEngine.Graphics.Null.TextureLayout`, `SomeEngine.Graphics.Null.TextureSubresourceEnumerator`
- SCC 149: `SomeEngine.Graphics.Null.CommandStream`, `SomeEngine.Graphics.Null.NullBuffer`, `SomeEngine.Graphics.Null.NullBufferView`, `SomeEngine.Graphics.Null.NullCommandList`, `SomeEngine.Graphics.Null.NullDescriptorHeap`, `SomeEngine.Graphics.Null.NullDescriptorSet`, `SomeEngine.Graphics.Null.NullDescriptorSetLayout`, `SomeEngine.Graphics.Null.NullDevice`, `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction`, `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedBuffer`, `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedQueryPool`, `SomeEngine.Graphics.Null.NullDevice.SubmissionTransaction.StagedTexture`, `SomeEngine.Graphics.Null.NullHeap`, `SomeEngine.Graphics.Null.NullPipeline`, `SomeEngine.Graphics.Null.NullPipelineLayout`, `SomeEngine.Graphics.Null.NullQueryPool`, `SomeEngine.Graphics.Null.NullSampler`, `SomeEngine.Graphics.Null.NullSwapchain`, `SomeEngine.Graphics.Null.NullTexture`, `SomeEngine.Graphics.Null.NullTextureView`, `SomeEngine.Graphics.Null.NullWorkGraph`
- SCC 152: `SomeEngine.RenderGraph.ArenaSlice<T>`, `SomeEngine.RenderGraph.ArenaSlice<T>.Enumerator`
- SCC 155: `SomeEngine.RenderGraph.ArenaColumn<T>`, `SomeEngine.RenderGraph.ArenaColumn<T>.Enumerator`
- SCC 202: `SomeEngine.RenderGraph.IComputeRenderGraphBuilder`, `SomeEngine.RenderGraph.IRasterRenderGraphBuilder`, `SomeEngine.RenderGraph.IUnsafeRenderGraphBuilder`, `SomeEngine.RenderGraph.PassExecutor`, `SomeEngine.RenderGraph.RasterScopeCompiler`, `SomeEngine.RenderGraph.ReachabilityTable`, `SomeEngine.RenderGraph.RenderGraph`, `SomeEngine.RenderGraph.RenderGraphBuilders`, `SomeEngine.RenderGraph.RenderGraphCompiler`, `SomeEngine.RenderGraph.RenderGraphCompiler.PassBarrierTable`, `SomeEngine.RenderGraph.RenderGraphCompiler.PassPredecessorTable`, `SomeEngine.RenderGraph.RenderGraphCompiler.TextureBarrierTracker`, `SomeEngine.RenderGraph.TransientPlacementCompiler`, `SomeEngine.RenderGraph.TransientPlacementCompiler.ResourceOccurrenceIndex`, `SomeEngine.RenderGraph.UnsafeGraphContext`
