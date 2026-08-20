namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null);

    /// <summary>
    /// Creates a Work Graph Pipeline asynchronously. Successful completion means the returned
    /// state object is ready for binding and dispatch.
    /// </summary>
    Task<Pipeline> CreateWorkGraphPipelineAsync(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null);
    WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline);
    bool TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount);
    void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization);
    void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc);
}
