using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Benchmarks;

internal interface IRhiDispatch<TReceiver>
{
    static abstract void Begin(TReceiver receiver, CommandContext context, in CommandRecordingDesc description);
    static abstract RecordedCommands End(TReceiver receiver, CommandContext context);
    static abstract void Barrier(TReceiver receiver, CommandContext context, in MemoryBarrier barrier);
    static abstract void Barrier(TReceiver receiver, CommandContext context, in BufferBarrier barrier);
    static abstract void Barrier(TReceiver receiver, CommandContext context, in TextureBarrier barrier);
    static abstract void Barrier(TReceiver receiver, CommandContext context, in QueueRelease barrier);
    static abstract void Barrier(TReceiver receiver, CommandContext context, in QueueAcquire barrier);
    static abstract void CopyBuffer(TReceiver receiver, CommandContext context, in BufferCopy copy);
    static abstract void CopyTexture(TReceiver receiver, CommandContext context, in TextureCopy copy);
    static abstract void CopyTextureToBuffer(TReceiver receiver, CommandContext context, in BufferTextureCopy copy);
    static abstract void BeginRendering(TReceiver receiver, CommandContext context, in RenderingDesc description);
    static abstract void EndRendering(TReceiver receiver, CommandContext context);
    static abstract void SetPipeline(TReceiver receiver, CommandContext context, Pipeline pipeline);
    static abstract void SetPersistentBindings(TReceiver receiver, CommandContext context, PersistentParameterBindings bindings);
    static abstract void SetTransientBindings(TReceiver receiver, CommandContext context, in ParameterBlockBindings bindings);
    static abstract void SetViewports(TReceiver receiver, CommandContext context, ReadOnlySpan<Viewport> viewports);
    static abstract void SetScissors(TReceiver receiver, CommandContext context, ReadOnlySpan<ScissorRect> scissors);
    static abstract void Draw(TReceiver receiver, CommandContext context, in DrawArguments arguments);
    static abstract void DrawRepeated(TReceiver receiver, CommandContext context, in DrawArguments arguments, int count);
    static abstract void DrawTransientPackets(TReceiver receiver, CommandContext context, VariableLayoutReflection layout, byte[] packets, in DrawArguments arguments, int count);
    static abstract void DrawWithRedundantState(TReceiver receiver, CommandContext context, Pipeline pipeline, PersistentParameterBindings bindings, Viewport[] viewports, ScissorRect[] scissors, in DrawArguments arguments, int count);
    static abstract void RecordMemoryBarriers(TReceiver receiver, CommandContext context, in MemoryBarrier barrier, int count);
    static abstract void Dispatch(TReceiver receiver, CommandContext context, in DispatchArguments arguments);
    static abstract void WriteTimestamp(TReceiver receiver, CommandContext context, QueryPool pool, uint index);
    static abstract void ResolveQueries(TReceiver receiver, CommandContext context, QueryPool pool, uint first, uint count, Buffer destination, in BufferRange range);
    static abstract QueueCompletion Submit(TReceiver receiver, Queue queue, in QueueSubmitDesc description);
    static abstract WaitStatus WaitCpu(TReceiver receiver, in QueueCompletion completion, TimeSpan timeout);
    static abstract void CollectCompleted(TReceiver receiver, Device device);
    static abstract CalibratedTimestampInfo Calibrate(TReceiver receiver, Queue queue);
    static abstract SwapchainAcquireStatus Acquire(TReceiver receiver, Swapchain swapchain, in SwapchainAcquireOptions options, out SwapchainImage image);
    static abstract PresentStatus Present(TReceiver receiver, Queue queue, in SwapchainImage image);
    static abstract MappedBuffer Map(TReceiver receiver, Buffer buffer, MapType type, in BufferRange range);
}

internal readonly struct GenericRhiDispatch : IRhiDispatch<GenericRhiDispatch>
{
    private readonly Graphics<D3D12Backend> _receiver;
    private static readonly ResourceBinding[] EmptyResources = [];

    internal GenericRhiDispatch(Graphics<D3D12Backend> receiver) => _receiver = receiver;

    public static void Begin(GenericRhiDispatch receiver, CommandContext context, in CommandRecordingDesc description) => receiver._receiver.Begin(context, description);
    public static RecordedCommands End(GenericRhiDispatch receiver, CommandContext context) => receiver._receiver.End(context);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Barrier(GenericRhiDispatch receiver, CommandContext context, in MemoryBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(GenericRhiDispatch receiver, CommandContext context, in BufferBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(GenericRhiDispatch receiver, CommandContext context, in TextureBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(GenericRhiDispatch receiver, CommandContext context, in QueueRelease barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(GenericRhiDispatch receiver, CommandContext context, in QueueAcquire barrier) => receiver._receiver.Barrier(context, barrier);
    public static void CopyBuffer(GenericRhiDispatch receiver, CommandContext context, in BufferCopy copy) => receiver._receiver.CopyBuffer(context, copy);
    public static void CopyTexture(GenericRhiDispatch receiver, CommandContext context, in TextureCopy copy) => receiver._receiver.CopyTexture(context, copy);
    public static void CopyTextureToBuffer(GenericRhiDispatch receiver, CommandContext context, in BufferTextureCopy copy) => receiver._receiver.CopyTextureToBuffer(context, copy);
    public static void BeginRendering(GenericRhiDispatch receiver, CommandContext context, in RenderingDesc description) => receiver._receiver.BeginRendering(context, description);
    public static void EndRendering(GenericRhiDispatch receiver, CommandContext context) => receiver._receiver.EndRendering(context);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetPipeline(GenericRhiDispatch receiver, CommandContext context, Pipeline pipeline) => receiver._receiver.SetPipeline(context, pipeline);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetPersistentBindings(GenericRhiDispatch receiver, CommandContext context, PersistentParameterBindings bindings) => receiver._receiver.SetPersistentParameterBindings(context, bindings);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetTransientBindings(GenericRhiDispatch receiver, CommandContext context, in ParameterBlockBindings bindings) => receiver._receiver.SetTransientParameterBindings(context, bindings);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetViewports(GenericRhiDispatch receiver, CommandContext context, ReadOnlySpan<Viewport> viewports) => receiver._receiver.SetViewports(context, viewports);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetScissors(GenericRhiDispatch receiver, CommandContext context, ReadOnlySpan<ScissorRect> scissors) => receiver._receiver.SetScissors(context, scissors);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Draw(GenericRhiDispatch receiver, CommandContext context, in DrawArguments arguments) => receiver._receiver.Draw(context, arguments);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void DrawRepeated(GenericRhiDispatch receiver, CommandContext context, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
            receiver._receiver.Draw(context, arguments);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void DrawTransientPackets(GenericRhiDispatch receiver, CommandContext context, VariableLayoutReflection layout, byte[] packets, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
        {
            ParameterBlockBindings packet = new(layout, EmptyResources, packets.AsSpan(index * 16, 16));
            receiver._receiver.SetTransientParameterBindings(context, packet);
            receiver._receiver.Draw(context, arguments);
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void DrawWithRedundantState(GenericRhiDispatch receiver, CommandContext context, Pipeline pipeline, PersistentParameterBindings bindings, Viewport[] viewports, ScissorRect[] scissors, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
        {
            receiver._receiver.SetPipeline(context, pipeline);
            receiver._receiver.SetPersistentParameterBindings(context, bindings);
            receiver._receiver.SetViewports(context, viewports);
            receiver._receiver.SetScissors(context, scissors);
            receiver._receiver.Draw(context, arguments);
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void RecordMemoryBarriers(GenericRhiDispatch receiver, CommandContext context, in MemoryBarrier barrier, int count)
    {
        for (int index = 0; index < count; index++)
            receiver._receiver.Barrier(context, barrier);
    }
    public static void Dispatch(GenericRhiDispatch receiver, CommandContext context, in DispatchArguments arguments) => receiver._receiver.Dispatch(context, arguments);
    public static void WriteTimestamp(GenericRhiDispatch receiver, CommandContext context, QueryPool pool, uint index) => receiver._receiver.WriteTimestamp(context, pool, index);
    public static void ResolveQueries(GenericRhiDispatch receiver, CommandContext context, QueryPool pool, uint first, uint count, Buffer destination, in BufferRange range) => receiver._receiver.ResolveQueries(context, pool, first, count, destination, range);
    public static QueueCompletion Submit(GenericRhiDispatch receiver, Queue queue, in QueueSubmitDesc description) => receiver._receiver.Submit(queue, description);
    public static WaitStatus WaitCpu(GenericRhiDispatch receiver, in QueueCompletion completion, TimeSpan timeout) => receiver._receiver.WaitCpu(completion, timeout);
    public static void CollectCompleted(GenericRhiDispatch receiver, Device device) => receiver._receiver.CollectCompleted(device);
    public static CalibratedTimestampInfo Calibrate(GenericRhiDispatch receiver, Queue queue) => receiver._receiver.CalibrateTimestamps(queue);
    public static SwapchainAcquireStatus Acquire(GenericRhiDispatch receiver, Swapchain swapchain, in SwapchainAcquireOptions options, out SwapchainImage image) => receiver._receiver.Acquire(swapchain, options, out image);
    public static PresentStatus Present(GenericRhiDispatch receiver, Queue queue, in SwapchainImage image) => receiver._receiver.Present(queue, image);
    public static MappedBuffer Map(GenericRhiDispatch receiver, Buffer buffer, MapType type, in BufferRange range) => receiver._receiver.Map(buffer, type, range);
}

internal readonly struct InterfaceRhiDispatch : IRhiDispatch<InterfaceRhiDispatch>
{
    private readonly IGraphicsBackend _receiver;
    private static readonly ResourceBinding[] EmptyResources = [];

    internal InterfaceRhiDispatch(IGraphicsBackend receiver) => _receiver = receiver;

    public static void Begin(InterfaceRhiDispatch receiver, CommandContext context, in CommandRecordingDesc description) => receiver._receiver.Begin(context, description);
    public static RecordedCommands End(InterfaceRhiDispatch receiver, CommandContext context) => receiver._receiver.End(context);
    public static void Barrier(InterfaceRhiDispatch receiver, CommandContext context, in MemoryBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceRhiDispatch receiver, CommandContext context, in BufferBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceRhiDispatch receiver, CommandContext context, in TextureBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceRhiDispatch receiver, CommandContext context, in QueueRelease barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceRhiDispatch receiver, CommandContext context, in QueueAcquire barrier) => receiver._receiver.Barrier(context, barrier);
    public static void CopyBuffer(InterfaceRhiDispatch receiver, CommandContext context, in BufferCopy copy) => receiver._receiver.CopyBuffer(context, copy);
    public static void CopyTexture(InterfaceRhiDispatch receiver, CommandContext context, in TextureCopy copy) => receiver._receiver.CopyTexture(context, copy);
    public static void CopyTextureToBuffer(InterfaceRhiDispatch receiver, CommandContext context, in BufferTextureCopy copy) => receiver._receiver.CopyTextureToBuffer(context, copy);
    public static void BeginRendering(InterfaceRhiDispatch receiver, CommandContext context, in RenderingDesc description) => receiver._receiver.BeginRendering(context, description);
    public static void EndRendering(InterfaceRhiDispatch receiver, CommandContext context) => receiver._receiver.EndRendering(context);
    public static void SetPipeline(InterfaceRhiDispatch receiver, CommandContext context, Pipeline pipeline) => receiver._receiver.SetPipeline(context, pipeline);
    public static void SetPersistentBindings(InterfaceRhiDispatch receiver, CommandContext context, PersistentParameterBindings bindings) => receiver._receiver.SetPersistentParameterBindings(context, bindings);
    public static void SetTransientBindings(InterfaceRhiDispatch receiver, CommandContext context, in ParameterBlockBindings bindings) => receiver._receiver.SetTransientParameterBindings(context, bindings);
    public static void SetViewports(InterfaceRhiDispatch receiver, CommandContext context, ReadOnlySpan<Viewport> viewports) => receiver._receiver.SetViewports(context, viewports);
    public static void SetScissors(InterfaceRhiDispatch receiver, CommandContext context, ReadOnlySpan<ScissorRect> scissors) => receiver._receiver.SetScissors(context, scissors);
    public static void Draw(InterfaceRhiDispatch receiver, CommandContext context, in DrawArguments arguments) => receiver._receiver.Draw(context, arguments);
    public static void DrawRepeated(InterfaceRhiDispatch receiver, CommandContext context, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
            receiver._receiver.Draw(context, arguments);
    }
    public static void DrawTransientPackets(InterfaceRhiDispatch receiver, CommandContext context, VariableLayoutReflection layout, byte[] packets, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
        {
            ParameterBlockBindings packet = new(layout, EmptyResources, packets.AsSpan(index * 16, 16));
            receiver._receiver.SetTransientParameterBindings(context, packet);
            receiver._receiver.Draw(context, arguments);
        }
    }
    public static void DrawWithRedundantState(InterfaceRhiDispatch receiver, CommandContext context, Pipeline pipeline, PersistentParameterBindings bindings, Viewport[] viewports, ScissorRect[] scissors, in DrawArguments arguments, int count)
    {
        for (int index = 0; index < count; index++)
        {
            receiver._receiver.SetPipeline(context, pipeline);
            receiver._receiver.SetPersistentParameterBindings(context, bindings);
            receiver._receiver.SetViewports(context, viewports);
            receiver._receiver.SetScissors(context, scissors);
            receiver._receiver.Draw(context, arguments);
        }
    }
    public static void RecordMemoryBarriers(InterfaceRhiDispatch receiver, CommandContext context, in MemoryBarrier barrier, int count)
    {
        for (int index = 0; index < count; index++)
            receiver._receiver.Barrier(context, barrier);
    }
    public static void Dispatch(InterfaceRhiDispatch receiver, CommandContext context, in DispatchArguments arguments) => receiver._receiver.Dispatch(context, arguments);
    public static void WriteTimestamp(InterfaceRhiDispatch receiver, CommandContext context, QueryPool pool, uint index) => receiver._receiver.WriteTimestamp(context, pool, index);
    public static void ResolveQueries(InterfaceRhiDispatch receiver, CommandContext context, QueryPool pool, uint first, uint count, Buffer destination, in BufferRange range) => receiver._receiver.ResolveQueries(context, pool, first, count, destination, range);
    public static QueueCompletion Submit(InterfaceRhiDispatch receiver, Queue queue, in QueueSubmitDesc description) => receiver._receiver.Submit(queue, description);
    public static WaitStatus WaitCpu(InterfaceRhiDispatch receiver, in QueueCompletion completion, TimeSpan timeout) => receiver._receiver.WaitCpu(completion, timeout);
    public static void CollectCompleted(InterfaceRhiDispatch receiver, Device device) => receiver._receiver.CollectCompleted(device);
    public static CalibratedTimestampInfo Calibrate(InterfaceRhiDispatch receiver, Queue queue) => receiver._receiver.CalibrateTimestamps(queue);
    public static SwapchainAcquireStatus Acquire(InterfaceRhiDispatch receiver, Swapchain swapchain, in SwapchainAcquireOptions options, out SwapchainImage image) => receiver._receiver.Acquire(swapchain, options, out image);
    public static PresentStatus Present(InterfaceRhiDispatch receiver, Queue queue, in SwapchainImage image) => receiver._receiver.Present(queue, image);
    public static MappedBuffer Map(InterfaceRhiDispatch receiver, Buffer buffer, MapType type, in BufferRange range) => receiver._receiver.Map(buffer, type, range);
}
