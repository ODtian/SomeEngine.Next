using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Benchmarks;

internal interface IRhiDispatch<TReceiver>
{
    static abstract void Begin(TReceiver receiver, CommandContext context, in CommandRecordingDesc description);
    static abstract RecordedCommands End(TReceiver receiver, CommandContext context);
#if SOMEENGINE_RHI_BENCHMARK_TIMING
    static abstract void CloseCommandsForBenchmark(TReceiver receiver, CommandContext context);
    static abstract RecordedCommands FinishCommandsForBenchmark(TReceiver receiver, CommandContext context);
#endif
    static abstract void Discard(TReceiver receiver, CommandContext context);
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

internal readonly struct InterfaceReceiverDispatch : IRhiDispatch<InterfaceReceiverDispatch>
{
    private readonly IGraphicsBackend _receiver;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
    private readonly IBenchmarkCommandTiming _benchmarkTiming;
#endif
    private static readonly ResourceBinding[] EmptyResources = [];

    internal InterfaceReceiverDispatch(IGraphicsBackend receiver)
    {
        _receiver = receiver;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
        _benchmarkTiming = (IBenchmarkCommandTiming)receiver;
#endif
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Begin(InterfaceReceiverDispatch receiver, CommandContext context, in CommandRecordingDesc description) => receiver._receiver.Begin(context, description);
    public static RecordedCommands End(InterfaceReceiverDispatch receiver, CommandContext context) => receiver._receiver.End(context);
#if SOMEENGINE_RHI_BENCHMARK_TIMING
    public static void CloseCommandsForBenchmark(InterfaceReceiverDispatch receiver, CommandContext context) => receiver._benchmarkTiming.CloseCommandsForBenchmark(context);
    public static RecordedCommands FinishCommandsForBenchmark(InterfaceReceiverDispatch receiver, CommandContext context) => receiver._benchmarkTiming.FinishCommandsForBenchmark(context);
#endif
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Discard(InterfaceReceiverDispatch receiver, CommandContext context) => receiver._receiver.Discard(context);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Barrier(InterfaceReceiverDispatch receiver, CommandContext context, in MemoryBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceReceiverDispatch receiver, CommandContext context, in BufferBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceReceiverDispatch receiver, CommandContext context, in TextureBarrier barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceReceiverDispatch receiver, CommandContext context, in QueueRelease barrier) => receiver._receiver.Barrier(context, barrier);
    public static void Barrier(InterfaceReceiverDispatch receiver, CommandContext context, in QueueAcquire barrier) => receiver._receiver.Barrier(context, barrier);
    public static void CopyBuffer(InterfaceReceiverDispatch receiver, CommandContext context, in BufferCopy copy) => receiver._receiver.CopyBuffer(context, copy);
    public static void CopyTexture(InterfaceReceiverDispatch receiver, CommandContext context, in TextureCopy copy) => receiver._receiver.CopyTexture(context, copy);
    public static void CopyTextureToBuffer(InterfaceReceiverDispatch receiver, CommandContext context, in BufferTextureCopy copy) => receiver._receiver.CopyTextureToBuffer(context, copy);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void BeginRendering(InterfaceReceiverDispatch receiver, CommandContext context, in RenderingDesc description) => receiver._receiver.BeginRendering(context, description);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void EndRendering(InterfaceReceiverDispatch receiver, CommandContext context) => receiver._receiver.EndRendering(context);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetPipeline(InterfaceReceiverDispatch receiver, CommandContext context, Pipeline pipeline) => receiver._receiver.SetPipeline(context, pipeline);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetPersistentBindings(InterfaceReceiverDispatch receiver, CommandContext context, PersistentParameterBindings bindings) => receiver._receiver.SetPersistentParameterBindings(context, bindings);
    public static void SetTransientBindings(InterfaceReceiverDispatch receiver, CommandContext context, in ParameterBlockBindings bindings) => receiver._receiver.SetTransientParameterBindings(context, bindings);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetViewports(InterfaceReceiverDispatch receiver, CommandContext context, ReadOnlySpan<Viewport> viewports) => receiver._receiver.SetViewports(context, viewports);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void SetScissors(InterfaceReceiverDispatch receiver, CommandContext context, ReadOnlySpan<ScissorRect> scissors) => receiver._receiver.SetScissors(context, scissors);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Draw(InterfaceReceiverDispatch receiver, CommandContext context, in DrawArguments arguments) => receiver._receiver.Draw(context, arguments);
    public static void DrawRepeated(InterfaceReceiverDispatch receiver, CommandContext context, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
            receiver._receiver.Draw(context, stableArguments);
    }
    public static void DrawTransientPackets(InterfaceReceiverDispatch receiver, CommandContext context, VariableLayoutReflection layout, byte[] packets, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
        {
            ParameterBlockBindings packet = new(layout, EmptyResources, packets.AsSpan(index * 16, 16));
            receiver._receiver.SetTransientParameterBindings(context, packet);
            receiver._receiver.Draw(context, stableArguments);
        }
    }
    public static void DrawWithRedundantState(InterfaceReceiverDispatch receiver, CommandContext context, Pipeline pipeline, PersistentParameterBindings bindings, Viewport[] viewports, ScissorRect[] scissors, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
        {
            receiver._receiver.SetPipeline(context, pipeline);
            receiver._receiver.SetPersistentParameterBindings(context, bindings);
            receiver._receiver.SetViewports(context, viewports);
            receiver._receiver.SetScissors(context, scissors);
            receiver._receiver.Draw(context, stableArguments);
        }
    }
    public static void RecordMemoryBarriers(InterfaceReceiverDispatch receiver, CommandContext context, in MemoryBarrier barrier, int count)
    {
        for (int index = 0; index < count; index++)
            receiver._receiver.Barrier(context, barrier);
    }
    public static void Dispatch(InterfaceReceiverDispatch receiver, CommandContext context, in DispatchArguments arguments) => receiver._receiver.Dispatch(context, arguments);
    public static void WriteTimestamp(InterfaceReceiverDispatch receiver, CommandContext context, QueryPool pool, uint index) => receiver._receiver.WriteTimestamp(context, pool, index);
    public static void ResolveQueries(InterfaceReceiverDispatch receiver, CommandContext context, QueryPool pool, uint first, uint count, Buffer destination, in BufferRange range) => receiver._receiver.ResolveQueries(context, pool, first, count, destination, range);
    public static QueueCompletion Submit(InterfaceReceiverDispatch receiver, Queue queue, in QueueSubmitDesc description) => receiver._receiver.Submit(queue, description);
    public static WaitStatus WaitCpu(InterfaceReceiverDispatch receiver, in QueueCompletion completion, TimeSpan timeout) => receiver._receiver.WaitCpu(completion, timeout);
    public static void CollectCompleted(InterfaceReceiverDispatch receiver, Device device) => receiver._receiver.CollectCompleted(device);
    public static CalibratedTimestampInfo Calibrate(InterfaceReceiverDispatch receiver, Queue queue) => receiver._receiver.CalibrateTimestamps(queue);
    public static SwapchainAcquireStatus Acquire(InterfaceReceiverDispatch receiver, Swapchain swapchain, in SwapchainAcquireOptions options, out SwapchainImage image) => receiver._receiver.Acquire(swapchain, options, out image);
    public static PresentStatus Present(InterfaceReceiverDispatch receiver, Queue queue, in SwapchainImage image) => receiver._receiver.Present(queue, image);
    public static MappedBuffer Map(InterfaceReceiverDispatch receiver, Buffer buffer, MapType type, in BufferRange range) => receiver._receiver.Map(buffer, type, range);
}
