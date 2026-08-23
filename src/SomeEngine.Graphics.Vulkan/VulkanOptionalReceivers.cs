namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    RhiSurface IGraphicsBackend.CreateSurface(in SurfaceDesc desc) =>
        CreateSurfaceCore(desc);

    Swapchain IGraphicsBackend.CreateSwapchain(RhiDevice device, in SwapchainDesc desc) =>
        CreateSwapchainCore(device, desc);
    SwapchainAcquireStatus IGraphicsBackend.Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        return AcquireCore(swapchain, options, out image);
    }
    PresentStatus IGraphicsBackend.Present(RhiQueue queue, in SwapchainImage image) =>
        PresentCore(queue, image);
    ReconfigureStatus IGraphicsBackend.Reconfigure(Swapchain swapchain, in SwapchainConfig config) =>
        ReconfigureCore(swapchain, config);

    SomeEngine.Graphics.PipelineCache IGraphicsBackend.CreatePipelineCache(
        RhiDevice device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken) =>
        CreatePipelineCacheCore(device, desc, cancellationToken);
    bool IGraphicsBackend.TryGetPipelineCacheData(
        SomeEngine.Graphics.PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken)
    {
        return TryGetPipelineCacheDataCore(cache, destination, out requiredByteCount, cancellationToken);
    }
    void IGraphicsBackend.MergePipelineCaches(
        SomeEngine.Graphics.PipelineCache destination,
        ReadOnlySpan<SomeEngine.Graphics.PipelineCache> sources,
        CancellationToken cancellationToken) =>
        MergePipelineCachesCore(destination, sources, cancellationToken);

    QueryPool IGraphicsBackend.CreateQueryPool(RhiDevice device, in QueryPoolDesc desc) =>
        CreateQueryPoolCore(device, desc);
    void IGraphicsBackend.BeginQuery(CommandContext context, QueryPool pool, uint queryIndex) =>
        BeginQueryCore(context, pool, queryIndex);
    void IGraphicsBackend.EndQuery(CommandContext context, QueryPool pool, uint queryIndex) =>
        EndQueryCore(context, pool, queryIndex);
    void IGraphicsBackend.WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex) =>
        WriteTimestampCore(context, pool, queryIndex);
    void IGraphicsBackend.ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        RhiBuffer destination,
        in BufferRange destinationRange) =>
        ResolveQueriesCore(context, pool, firstQuery, queryCount, destination, destinationRange);

    IndirectCommandLayout IGraphicsBackend.CreateIndirectCommandLayout(
        RhiDevice device,
        in IndirectCommandLayoutDesc desc) =>
        CreateIndirectCommandLayoutCore(device, desc);
    void IGraphicsBackend.ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count) =>
        ExecuteIndirectCore(context, layout, arguments, maximumCommandCount, count);

    Pipeline IGraphicsBackend.CreateMeshPipeline(
        RhiDevice device,
        in MeshPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        CreateMeshPipelineCore(device, desc, cache);
    Task<Pipeline> IGraphicsBackend.CreateMeshPipelineAsync(
        RhiDevice device,
        in MeshPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        CreateMeshPipelineAsyncCore(device, desc, cache);
    void IGraphicsBackend.DispatchMesh(CommandContext context, in DispatchArguments arguments) =>
        DispatchMeshCore(context, arguments);
    void IGraphicsBackend.DispatchMeshIndirect(CommandContext context, in BufferRegion arguments) =>
        DispatchMeshIndirectCore(context, arguments);
    void IGraphicsBackend.SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner) =>
        SetShadingRateCore(context, rate, primitiveCombiner, imageCombiner);
    void IGraphicsBackend.SetShadingRateImage(CommandContext context, RhiTexture? texture) =>
        SetShadingRateImageCore(context, texture);

    CalibratedTimestampInfo IGraphicsBackend.CalibrateTimestamps(RhiQueue queue) =>
        CalibrateTimestampsCore(queue);
    RhiBuffer IGraphicsBackend.ImportBuffer(
        RhiDevice device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        ImportBufferCore(device, handle, desc, state);
    RhiTexture IGraphicsBackend.ImportTexture(
        RhiDevice device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        ImportTextureCore(device, handle, desc, state);
    RhiHeap IGraphicsBackend.ImportHeap(
        RhiDevice device,
        ExternalHandle handle,
        in HeapDesc desc) =>
        ImportHeapCore(device, handle, desc);
    ExternalHandle IGraphicsBackend.ExportBuffer(RhiBuffer buffer, ExternalHandleType type) =>
        ExportBufferCore(buffer, type);
    ExternalHandle IGraphicsBackend.ExportTexture(RhiTexture texture, ExternalHandleType type) =>
        ExportTextureCore(texture, type);
    ExternalHandle IGraphicsBackend.ExportHeap(RhiHeap heap, ExternalHandleType type) =>
        ExportHeapCore(heap, type);
    ExternalTimeline IGraphicsBackend.CreateExternalTimeline(
        RhiDevice device,
        ulong initialValue,
        string? label) =>
        CreateExternalTimelineCore(device, initialValue, label);
    ExternalTimeline IGraphicsBackend.ImportTimeline(
        RhiDevice device,
        ExternalHandle handle,
        string? label) =>
        ImportTimelineCore(device, handle, label);
    ExternalHandle IGraphicsBackend.ExportTimeline(
        ExternalTimeline timeline,
        ExternalHandleType type) =>
        ExportTimelineCore(timeline, type);

    ResidencyInfo IGraphicsBackend.GetResidencyInfo(RhiDevice device) =>
        GetResidencyInfoCore(device);
    ResidencyResource IGraphicsBackend.GetResidencyResource(RhiHeap heap) =>
        GetResidencyResourceCore(heap, heap.Device);
    ResidencyResource IGraphicsBackend.GetResidencyResource(Resource resource) =>
        GetResidencyResourceCore(resource, resource.Device);
    ResidencyResource IGraphicsBackend.GetResidencyResource(QueryPool pool) =>
        GetResidencyResourceCore(pool, pool.Device);
    ResidencyResource IGraphicsBackend.GetResidencyResource(DescriptorTable table) =>
        GetResidencyResourceCore(table, table.Device);
    QueueCompletion IGraphicsBackend.EnqueueMakeResident(
        RhiQueue queue,
        ReadOnlySpan<ResidencyResource> resources) =>
        EnqueueMakeResidentCore(queue, resources);
    void IGraphicsBackend.Evict(RhiDevice device, ReadOnlySpan<ResidencyResource> resources) =>
        EvictCore(device, resources);

    RhiBuffer IGraphicsBackend.CreateReservedBuffer(RhiDevice device, in BufferDesc desc) =>
        CreateReservedBufferCore(device, desc);
    RhiTexture IGraphicsBackend.CreateReservedTexture(RhiDevice device, in TextureDesc desc) =>
        CreateReservedTextureCore(device, desc);
    SparseResourceInfo IGraphicsBackend.GetSparseResourceInfo(Resource resource) =>
        GetSparseResourceInfoCore(resource);
    QueueCompletion IGraphicsBackend.UpdateSparseMappings(
        RhiQueue queue,
        ReadOnlySpan<SparseMappingDesc> mappings) =>
        UpdateSparseMappingsCore(queue, mappings);
    QueueCompletion IGraphicsBackend.CopySparseMappings(
        RhiQueue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies) =>
        CopySparseMappingsCore(queue, copies);

    AccelerationStructure IGraphicsBackend.CreateAccelerationStructure(
        RhiDevice device,
        RhiBuffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label) =>
        CreateAccelerationStructureCore(device, storage, storageRange, type, label);
    AccelerationStructureSrv IGraphicsBackend.CreateAccelerationStructureSrv(
        RhiDevice device,
        in AccelerationStructureSrvDesc desc) =>
        CreateAccelerationStructureSrvCore(device, desc);
    AccelerationStructureBuildInfo IGraphicsBackend.GetAccelerationStructureBuildInfo(
        RhiDevice device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries) =>
        GetAccelerationStructureBuildInfoCore(device, type, options, geometries);
    Pipeline IGraphicsBackend.CreateRayTracingPipeline(
        RhiDevice device,
        in RayTracingPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        CreateRayTracingPipelineCore(device, desc, cache);
    Task<Pipeline> IGraphicsBackend.CreateRayTracingPipelineAsync(
        RhiDevice device,
        in RayTracingPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        CreateRayTracingPipelineAsyncCore(device, desc, cache);
    RayTracingShaderTable IGraphicsBackend.CreateRayTracingShaderTable(
        RhiDevice device,
        in RayTracingShaderTableDesc desc) =>
        CreateRayTracingShaderTableCore(device, desc);
    void IGraphicsBackend.BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc) =>
        BuildAccelerationStructureCore(context, desc);
    void IGraphicsBackend.CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type) =>
        CopyAccelerationStructureCore(context, destination, source, type);
    void IGraphicsBackend.SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source) =>
        SerializeAccelerationStructureCore(context, destination, source);
    void IGraphicsBackend.DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source) =>
        DeserializeAccelerationStructureCore(context, destination, source);
    void IGraphicsBackend.EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        RhiBuffer destination,
        ulong destinationOffset) =>
        EmitAccelerationStructurePostBuildInfoCore(context, source, type, destination, destinationOffset);
    void IGraphicsBackend.UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update) =>
        UpdateRayTracingShaderTableCore(context, table, update);
    void IGraphicsBackend.DispatchRays(CommandContext context, in DispatchRaysDesc desc) =>
        DispatchRaysCore(context, desc);

    SamplerFeedbackTexture IGraphicsBackend.CreateSamplerFeedbackTexture(
        RhiDevice device,
        in SamplerFeedbackTextureDesc desc) =>
        throw Missing("sampler feedback");
    SamplerFeedbackUav IGraphicsBackend.CreateSamplerFeedbackUav(
        RhiDevice device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc) =>
        throw Missing("sampler feedback");
    void IGraphicsBackend.ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback) =>
        throw Missing("sampler feedback");
    void IGraphicsBackend.ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        RhiBuffer destination,
        in BufferRange destinationRange) =>
        throw Missing("sampler feedback");
    void IGraphicsBackend.ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        RhiTexture destination,
        in TextureSubresourceRange destinationRange) =>
        throw Missing("sampler feedback");

    Pipeline IGraphicsBackend.CreateWorkGraphPipeline(
        RhiDevice device,
        in WorkGraphPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        throw Missing("shader enqueue/work graphs");
    Task<Pipeline> IGraphicsBackend.CreateWorkGraphPipelineAsync(
        RhiDevice device,
        in WorkGraphPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache) =>
        Task.FromException<Pipeline>(Missing("shader enqueue/work graphs"));
    WorkGraphMemoryRequirements IGraphicsBackend.GetWorkGraphMemoryRequirements(Pipeline pipeline) =>
        throw Missing("shader enqueue/work graphs");
    bool IGraphicsBackend.TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount)
    {
        requiredCount = 0;
        throw Missing("shader enqueue/work graphs");
    }
    void IGraphicsBackend.BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization) =>
        throw Missing("shader enqueue/work graphs");
    void IGraphicsBackend.DispatchWorkGraph(
        CommandContext context,
        in WorkGraphDispatchDesc desc) =>
        throw Missing("shader enqueue/work graphs");

    private static NotSupportedException Missing(string feature) => new(
        $"The Vulkan {feature} implementation is not available on the selected Device.");
}
