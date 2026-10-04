using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

// Injects failure at the public backend allocation boundary while using real device pipelines.
internal sealed class SamplerFailureBackend(IGraphicsBackend backend) : IGraphicsBackend
{
    internal Pipeline? GlyphPipeline { get; private set; }
    internal Exception Error { get; } = new InvalidOperationException("sampler creation failed");

    public Pipeline CreateGraphicsPipeline(Device device, in GraphicsPipelineDesc desc, PipelineCache? cache = null)
    {
        Pipeline pipeline = backend.CreateGraphicsPipeline(device, desc, cache);
        if (desc.Label == "Glyphs") GlyphPipeline = pipeline;
        return pipeline;
    }

    public Sampler CreateSampler(Device device, in SamplerDesc desc) => throw Error;
    public void Dispose() { }




    public bool IsComplete(in QueueCompletion completion) => throw new NotSupportedException();

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout) => throw new NotSupportedException();

    public CommandContext CreateCommandContext(Device device, in CommandContextDesc desc) => throw new NotSupportedException();

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default) => throw new NotSupportedException();

    public RecordedCommands End(CommandContext context) => throw new NotSupportedException();

    public RecordedBundle EndBundle(CommandContext context) => throw new NotSupportedException();

    public void Discard(CommandContext context) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in MemoryBarrier barrier) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in BufferBarrier barrier) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in TextureBarrier barrier) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in AliasingBarrier barrier) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in QueueRelease barrier) => throw new NotSupportedException();

    public void Barrier(CommandContext context, in QueueAcquire barrier) => throw new NotSupportedException();

    public void CopyBuffer(CommandContext context, in BufferCopy copy) => throw new NotSupportedException();

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy) => throw new NotSupportedException();

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy) => throw new NotSupportedException();

    public void CopyTexture(CommandContext context, in TextureCopy copy) => throw new NotSupportedException();

    public void ResolveTexture(CommandContext context, in TextureResolve resolve) => throw new NotSupportedException();

    public void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0) => throw new NotSupportedException();

    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in Vector4 color) => throw new NotSupportedException();

    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0) => throw new NotSupportedException();

    public void BeginRendering(CommandContext context, in RenderingDesc desc) => throw new NotSupportedException();

    public void EndRendering(CommandContext context) => throw new NotSupportedException();

    public void SetPipeline(CommandContext context, Pipeline pipeline) => throw new NotSupportedException();

    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings) => throw new NotSupportedException();

    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings) => throw new NotSupportedException();

    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings) => throw new NotSupportedException();

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding) => throw new NotSupportedException();

    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings) => throw new NotSupportedException();

    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports) => throw new NotSupportedException();

    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors) => throw new NotSupportedException();

    public void SetBlendConstants(CommandContext context, in Vector4 value) => throw new NotSupportedException();

    public void SetStencilReference(CommandContext context, uint value) => throw new NotSupportedException();

    public void SetDepthBounds(CommandContext context, float minimum, float maximum) => throw new NotSupportedException();

    public void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias) => throw new NotSupportedException();

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology) => throw new NotSupportedException();

    public void SetStripCut(CommandContext context, StripCut stripCut) => throw new NotSupportedException();

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero) => throw new NotSupportedException();

    public void Draw(CommandContext context, in DrawArguments arguments) => throw new NotSupportedException();

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments) => throw new NotSupportedException();

    public void Dispatch(CommandContext context, in DispatchArguments arguments) => throw new NotSupportedException();

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle) => throw new NotSupportedException();

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label) => throw new NotSupportedException();

    public void EndEvent(CommandContext context) => throw new NotSupportedException();

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label) => throw new NotSupportedException();

    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc) => throw new NotSupportedException();

    public Swapchain CreateSwapchain(Device device, in SwapchainDesc desc) => throw new NotSupportedException();

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image) => throw new NotSupportedException();

    public PresentStatus Present(Queue queue, in SwapchainImage image) => throw new NotSupportedException();

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config) => throw new NotSupportedException();

    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Task<Pipeline> CreateWorkGraphPipelineAsync(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline) => throw new NotSupportedException();

    public bool TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount) => throw new NotSupportedException();

    public void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization) => throw new NotSupportedException();

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc) => throw new NotSupportedException();

    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc) => throw new NotSupportedException();

    public Texture CreateReservedTexture(Device device, in TextureDesc desc) => throw new NotSupportedException();

    public SparseResourceInfo GetSparseResourceInfo(Resource resource) => throw new NotSupportedException();

    public QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings) => throw new NotSupportedException();

    public QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies) => throw new NotSupportedException();

    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc) => throw new NotSupportedException();

    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc) => throw new NotSupportedException();

    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback) => throw new NotSupportedException();

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange) => throw new NotSupportedException();

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange) => throw new NotSupportedException();

    public ResidencyInfo GetResidencyInfo(Device device) => throw new NotSupportedException();

    public ResidencyResource GetResidencyResource(Heap heap) => throw new NotSupportedException();

    public ResidencyResource GetResidencyResource(Resource resource) => throw new NotSupportedException();

    public ResidencyResource GetResidencyResource(QueryPool pool) => throw new NotSupportedException();

    public ResidencyResource GetResidencyResource(DescriptorTable table) => throw new NotSupportedException();

    public QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources) => throw new NotSupportedException();

    public void Evict(Device device, ReadOnlySpan<ResidencyResource> resources) => throw new NotSupportedException();

    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null) => throw new NotSupportedException();

    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc) => throw new NotSupportedException();

    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries) => throw new NotSupportedException();

    public Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Task<Pipeline> CreateRayTracingPipelineAsync(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc) => throw new NotSupportedException();

    public void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc) => throw new NotSupportedException();

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type) => throw new NotSupportedException();

    public void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source) => throw new NotSupportedException();

    public void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source) => throw new NotSupportedException();

    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset) => throw new NotSupportedException();

    public void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update) => throw new NotSupportedException();

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc) => throw new NotSupportedException();

    public CalibratedTimestampInfo CalibrateTimestamps(Queue queue) => throw new NotSupportedException();

    public Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state) => throw new NotSupportedException();

    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state) => throw new NotSupportedException();

    public Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc) => throw new NotSupportedException();

    public ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type) => throw new NotSupportedException();

    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type) => throw new NotSupportedException();

    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type) => throw new NotSupportedException();

    public ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null) => throw new NotSupportedException();

    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null) => throw new NotSupportedException();

    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type) => throw new NotSupportedException();

    public void DispatchMesh(CommandContext context, in DispatchArguments arguments) => throw new NotSupportedException();

    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments) => throw new NotSupportedException();

    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner) => throw new NotSupportedException();

    public void SetShadingRateImage(CommandContext context, Texture? texture) => throw new NotSupportedException();

    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc) => throw new NotSupportedException();

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null) => throw new NotSupportedException();

    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal) => throw new NotSupportedException();

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc) => throw new NotSupportedException();

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0) => throw new NotSupportedException();

    public Heap CreateHeap(Device device, in HeapDesc desc) => throw new NotSupportedException();

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal) => throw new NotSupportedException();

    public Buffer CreatePlacedBuffer(
        Device device,
        Heap heap,
        ulong offset,
        in BufferDesc desc) => throw new NotSupportedException();

    public Texture CreateTexture(Device device, in TextureDesc desc) => throw new NotSupportedException();

    public Texture CreatePlacedTexture(
        Device device,
        Heap heap,
        ulong offset,
        in TextureDesc desc) => throw new NotSupportedException();

    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc) => throw new NotSupportedException();

    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc) => throw new NotSupportedException();

    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc) => throw new NotSupportedException();

    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc) => throw new NotSupportedException();

    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc) => throw new NotSupportedException();

    public ColorAttachmentView CreateColorAttachmentView(Device device, in ColorAttachmentViewDesc desc) => throw new NotSupportedException();

    public DepthStencilView CreateDepthStencilView(Device device, in DepthStencilViewDesc desc) => throw new NotSupportedException();

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range) => throw new NotSupportedException();

    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot) => throw new NotSupportedException();

    public void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value) => throw new NotSupportedException();

    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        Pipeline pipeline,
        in ParameterBlockBindings bindings,
        string? label = null) => throw new NotSupportedException();

    public void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings) => throw new NotSupportedException();

    public void PublishDescriptors(
        Device device,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc) => throw new NotSupportedException();

    public void ResetQueries(CommandContext context, QueryPool pool, uint firstQuery, uint queryCount) => throw new NotSupportedException();

    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex) => throw new NotSupportedException();

    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex) => throw new NotSupportedException();

    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex) => throw new NotSupportedException();

    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange) => throw new NotSupportedException();

    public Task<Pipeline> CreateGraphicsPipelineAsync(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Task<Pipeline> CreateComputePipelineAsync(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public Task<Pipeline> CreateMeshPipelineAsync(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null) => throw new NotSupportedException();

    public PipelineCache CreatePipelineCache(
        Device device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount) => throw new NotSupportedException();

    public Device CreateDevice(in DeviceDesc desc) => throw new NotSupportedException();

    public Surface CreateSurface(in SurfaceDesc desc) => throw new NotSupportedException();

    public Queue GetQueue(Device device, QueueType type, uint index = 0) => throw new NotSupportedException();

    public bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability => throw new NotSupportedException();

    public void CollectCompleted(Device device) => throw new NotSupportedException();
}
