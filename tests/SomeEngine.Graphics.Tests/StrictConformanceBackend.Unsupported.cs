namespace SomeEngine.Graphics.Tests;

internal sealed partial class StrictConformanceBackend
{
    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc) =>
        throw Unsupported(nameof(IndirectCommands));

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null) =>
        throw Unsupported(nameof(IndirectCommands));

    public void DispatchMesh(CommandContext context, in DispatchArguments arguments) =>
        throw Unsupported(nameof(MeshShaders));

    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments) =>
        throw Unsupported(nameof(MeshShaders));

    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner) =>
        throw Unsupported(nameof(VariableRateShading));

    public void SetShadingRateImage(CommandContext context, Texture? texture) =>
        throw Unsupported(nameof(VariableRateShading));

    public CalibratedTimestampInfo CalibrateTimestamps(Queue queue) =>
        throw Unsupported(nameof(CalibratedTimestamps));

    public Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        throw Unsupported(nameof(ExternalResources));

    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        throw Unsupported(nameof(ExternalResources));

    public Heap ImportHeap(Device device, ExternalHandle handle, in HeapDesc desc) =>
        throw Unsupported(nameof(ExternalResources));

    public ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type) =>
        throw Unsupported(nameof(ExternalResources));

    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type) =>
        throw Unsupported(nameof(ExternalResources));

    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type) =>
        throw Unsupported(nameof(ExternalResources));

    public ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null) =>
        throw Unsupported(nameof(ExternalTimelines));

    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null) =>
        throw Unsupported(nameof(ExternalTimelines));

    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type) =>
        throw Unsupported(nameof(ExternalTimelines));

    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null) =>
        throw Unsupported(nameof(RayTracing));

    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc) =>
        throw Unsupported(nameof(RayTracing));

    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries) =>
        throw Unsupported(nameof(RayTracing));

    public Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(RayTracing));

    public Task<Pipeline> CreateRayTracingPipelineAsync(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(RayTracing));

    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc) =>
        throw Unsupported(nameof(RayTracing));

    public void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc) =>
        throw Unsupported(nameof(RayTracing));

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type) =>
        throw Unsupported(nameof(RayTracing));

    public void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source) =>
        throw Unsupported(nameof(RayTracing));

    public void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source) =>
        throw Unsupported(nameof(RayTracing));

    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset) =>
        throw Unsupported(nameof(RayTracing));

    public void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update) =>
        throw Unsupported(nameof(RayTracing));

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc) =>
        throw Unsupported(nameof(RayTracing));

    public ResidencyInfo GetResidencyInfo(Device device) =>
        throw Unsupported(nameof(Residency));

    public ResidencyResource GetResidencyResource(Heap heap) =>
        throw Unsupported(nameof(Residency));

    public ResidencyResource GetResidencyResource(Resource resource) =>
        throw Unsupported(nameof(Residency));

    public ResidencyResource GetResidencyResource(QueryPool pool) =>
        throw Unsupported(nameof(Residency));

    public ResidencyResource GetResidencyResource(DescriptorTable table) =>
        throw Unsupported(nameof(Residency));

    public QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources) =>
        throw Unsupported(nameof(Residency));

    public void Evict(Device device, ReadOnlySpan<ResidencyResource> resources) =>
        throw Unsupported(nameof(Residency));

    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc) =>
        throw Unsupported(nameof(SamplerFeedback));

    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc) =>
        throw Unsupported(nameof(SamplerFeedback));

    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback) =>
        throw Unsupported(nameof(SamplerFeedback));

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange) =>
        throw Unsupported(nameof(SamplerFeedback));

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange) =>
        throw Unsupported(nameof(SamplerFeedback));

    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc) =>
        throw Unsupported(nameof(SparseResources));

    public Texture CreateReservedTexture(Device device, in TextureDesc desc) =>
        throw Unsupported(nameof(SparseResources));

    public SparseResourceInfo GetSparseResourceInfo(Resource resource) =>
        throw Unsupported(nameof(SparseResources));

    public QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings) =>
        throw Unsupported(nameof(SparseResources));

    public QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies) =>
        throw Unsupported(nameof(SparseResources));

    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(WorkGraphs));

    public Task<Pipeline> CreateWorkGraphPipelineAsync(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(WorkGraphs));

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline) =>
        throw Unsupported(nameof(WorkGraphs));

    public bool TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount)
    {
        requiredCount = 0;
        throw Unsupported(nameof(WorkGraphs));
    }

    public void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization) =>
        throw Unsupported(nameof(WorkGraphs));

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc) =>
        throw Unsupported(nameof(WorkGraphs));

    private static NotSupportedException Unsupported(string capability) =>
        new($"The strict conformance backend does not advertise {capability}.");
}
