using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null);
    WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline);
    void SetWorkGraphProgram(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion backingMemory,
        WorkGraphInitialization initialization,
        uint maximumInputRecordCount);
    void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null) =>
        Receiver.CreateWorkGraphPipeline(device, desc, cache);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline) =>
        Receiver.GetWorkGraphMemoryRequirements(pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorkGraphProgram(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion backingMemory,
        WorkGraphInitialization initialization,
        uint maximumInputRecordCount) =>
        Receiver.SetWorkGraphProgram(
            context,
            pipeline,
            backingMemory,
            initialization,
            maximumInputRecordCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc) =>
        Receiver.DispatchWorkGraph(context, desc);
}
