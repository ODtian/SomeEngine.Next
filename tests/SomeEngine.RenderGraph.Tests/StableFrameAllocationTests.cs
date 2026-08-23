using System.Numerics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class StableFrameAllocationTests
{
    private const ulong CopySize = 256;

    [Fact]
    public void EmptyPersistentFrameDoesNotAllocateAfterWarmup()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        for (int frameIndex = 0; frameIndex < 32; frameIndex++)
            ExecuteEmpty(graph, completions);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long beginAllocated = 0;
        long executeAllocated = 0;
        for (int frameIndex = 0; frameIndex < 128; frameIndex++)
        {
            long beforeBegin = GC.GetAllocatedBytesForCurrentThread();
            RenderGraphFrame frame = graph.BeginFrame();
            beginAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeBegin;
            long beforeExecute = GC.GetAllocatedBytesForCurrentThread();
            completions.AsSpan().Clear();
            if (frame.Execute(completions) != 0)
                throw new InvalidOperationException("An empty graph unexpectedly submitted GPU work.");
            executeAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeExecute;
            frame.Dispose();
        }
        long allocated = beginAllocated + executeAllocated;

        Assert.True(
            allocated == 0,
            $"A stable empty RenderGraph frame allocated {allocated} managed bytes across 128 frames " +
            $"(BeginFrame={beginAllocated}, Execute={executeAllocated}).");
    }

    [Fact]
    public void StableRegisteredCopyFrameDoesNotAllocateAfterWarmup()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(CopySize, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(CopySize, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(
                source,
                [Endpoint(source, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(
                destination,
                [Endpoint(destination, ResourceContentState.Undefined)]);
            CopyState state = new(sourceId, destinationId);
            _ = edit.AddCopyPass<CopyState, byte>(
                "stable registered copy",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                default,
                static (ref PassDefinition access, ref CopyState data) =>
                {
                    _ = access.Read(
                        data.Source,
                        new BufferRange(0, CopySize),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = access.Write(
                        data.Destination,
                        new BufferRange(0, CopySize),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyState data, in byte _) =>
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(data.Source),
                        0,
                        commands.GetBuffer(data.Destination),
                        0,
                        CopySize)));
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 32; frameIndex++)
            ExecuteCopy(graph, support, completions);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // A full collection can trigger one-time tiered-runtime and driver bookkeeping on
        // the next identical call. Prime the exact instrumented path again so the measured
        // interval represents the stable high-water path rather than post-GC initialization.
        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            ExecuteCopy(graph, support, completions);
        long beginAllocated = 0;
        long executeAllocated = 0;
        Span<int> allocationFrames = stackalloc int[16];
        Span<long> allocationBytes = stackalloc long[16];
        int allocationCount = 0;
        for (int frameIndex = 0; frameIndex < 128; frameIndex++)
        {
            completions.AsSpan().Clear();
            long beforeBegin = GC.GetAllocatedBytesForCurrentThread();
            RenderGraphFrame frame = graph.BeginFrame();
            beginAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeBegin;
            long beforeExecute = GC.GetAllocatedBytesForCurrentThread();
            if (frame.Execute(completions) != 1)
                throw new InvalidOperationException("The stable copy graph did not submit one Queue.");
            long frameAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeExecute;
            executeAllocated += frameAllocated;
            if (frameAllocated != 0 && allocationCount < allocationFrames.Length)
            {
                allocationFrames[allocationCount] = frameIndex;
                allocationBytes[allocationCount] = frameAllocated;
                allocationCount++;
            }
            frame.Dispose();
            support.Wait(completions);
        }
        long allocated = beginAllocated + executeAllocated;

        Assert.True(
            allocated == 0,
            $"A stable registered copy graph allocated {allocated} managed bytes across 128 frames " +
            $"(BeginFrame={beginAllocated}, Execute={executeAllocated}; " +
            $"nonzero={FormatAllocations(allocationFrames[..allocationCount], allocationBytes[..allocationCount])}).");
    }

    [Fact]
    public void CommittedEditInvalidatesStableTransientExecution()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                CopySize,
                BufferUsages.CopyDestination));
            AddClearPass(edit, support.GraphicsQueue, "first stable clear", buffer, 1);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions);

        RenderGraphFrameOptions alternateOptions = new(
            FrameSubmissionMode.RecordAllThenSubmit,
            RenderGraphDebugOptions.DeclarationOrderScheduling |
            RenderGraphDebugOptions.DisableParallelRecording);
        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions, alternateOptions);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                CopySize,
                BufferUsages.CopyDestination));
            AddClearPass(edit, support.GraphicsQueue, "clear added after stable reuse", buffer, 2);
            edit.Commit();
        }

        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions);
    }

    [Fact]
    public void StableTransientTextureExecutesAfterPlanReuse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreateTransientTexture(new TextureDesc(
                TextureDimension.Texture2D,
                width: 16,
                height: 16,
                depth: 1,
                mipLevelCount: 1,
                arrayLayerCount: 1,
                sampleCount: 1,
                format: Format.R8G8B8A8UNorm,
                usages: TextureUsages.ColorAttachment));
            _ = edit.AddCopyPass<ClearTextureState, byte>(
                "stable transient texture clear",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ClearTextureState(texture, range),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition access, ref ClearTextureState state) =>
                    _ = access.Write(
                        state.Texture,
                        state.Range,
                        PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget,
                        TextureLayout.RenderTarget,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in ClearTextureState state, in byte _) =>
                    commands.ClearTexture(
                        commands.GetTexture(state.Texture),
                        state.Range,
                        new Vector4(0.25f, 0.5f, 0.75f, 1.0f)));
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 16; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions);
    }

    [Fact]
    public void StableWorkerEligiblePassesExecuteAcrossCoarseRecordingBatches()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int index = 0; index < 32; index++)
            {
                GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                    CopySize,
                    BufferUsages.CopyDestination));
                AddWorkerClearPass(edit, support.GraphicsQueue, $"worker clear {index}", buffer, (uint)index);
            }
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        RenderGraphStatistics statistics = default;
        ExecuteSubmittedFrame(
            graph,
            support,
            completions,
            new RenderGraphFrameOptions(Diagnostics: CaptureStatistics));
        Assert.Equal(32, statistics.ScheduledPassCount);
        Assert.Equal(1, statistics.QueueCount);
        for (int frameIndex = 1; frameIndex < 8; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions);

        void CaptureStatistics(in RenderGraphDiagnosticsView diagnostics) =>
            statistics = diagnostics.Statistics;
    }

    [Fact]
    public void FirstUseCompleteBufferWriteDoesNotEmitAnEmptyTransition()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                CopySize,
                BufferUsages.CopyDestination));
            AddClearPass(edit, support.GraphicsQueue, "first complete buffer write", buffer, 1);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        RenderGraphStatistics statistics = default;
        ExecuteSubmittedFrame(
            graph,
            support,
            completions,
            new RenderGraphFrameOptions(Diagnostics: CaptureStatistics));

        Assert.Equal(0, statistics.BarrierCount);

        void CaptureStatistics(in RenderGraphDiagnosticsView diagnostics) =>
            statistics = diagnostics.Statistics;
    }

    [Fact]
    public void StableRasterAttachmentDescriptionIsReusable()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
            GraphTextureId texture = edit.CreateTransientTexture(new TextureDesc(
                TextureDimension.Texture2D,
                16,
                16,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
            GraphColorAttachmentViewId view = edit.CreateColorAttachmentView(
                texture,
                range,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D);
            _ = edit.AddRasterPass<GraphColorAttachmentViewId, byte>(
                "stable raster attachment",
                PassQueueSelection.Exact(support.GraphicsQueue),
                view,
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                    access.ColorAttachment(
                        0,
                        target,
                        LoadType.Clear,
                        StoreType.Store,
                        WriteCoverage.Complete,
                        new Vector4(0.125f, 0.25f, 0.5f, 1f)),
                static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            ExecuteSubmittedFrame(graph, support, completions);
    }

    private static void ExecuteEmpty(RenderGraph graph, QueueCompletion[] completions)
    {
        completions.AsSpan().Clear();
        using RenderGraphFrame frame = graph.BeginFrame();
        if (frame.Execute(completions) != 0)
            throw new InvalidOperationException("An empty graph unexpectedly submitted GPU work.");
    }

    private static void ExecuteCopy(
        RenderGraph graph,
        WarpGraphTestSupport support,
        QueueCompletion[] completions)
    {
        completions.AsSpan().Clear();
        using RenderGraphFrame frame = graph.BeginFrame();
        if (frame.Execute(completions) != 1)
            throw new InvalidOperationException("The stable copy graph did not submit one Queue.");
        support.Wait(completions);
    }

    private static void ExecuteSubmittedFrame(
        RenderGraph graph,
        WarpGraphTestSupport support,
        QueueCompletion[] completions,
        RenderGraphFrameOptions options = default)
    {
        completions.AsSpan().Clear();
        using RenderGraphFrame frame = graph.BeginFrame(options);
        if (frame.Execute(completions) != 1)
            throw new InvalidOperationException("The transient graph did not submit one Queue.");
        support.Wait(completions);
    }

    private static void AddClearPass(
        RenderGraphEdit edit,
        Queue queue,
        string label,
        GraphBufferId buffer,
        uint value)
    {
        _ = edit.AddCopyPass<ClearState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            new ClearState(buffer, value),
            new PassOptions(
                Culling: PassCullingMode.NeverCull,
                Recording: PassRecordingMode.CallingThread),
            static (ref PassDefinition access, ref ClearState state) =>
                _ = access.Write(
                    state.Buffer,
                    new BufferRange(0, CopySize),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in ClearState state, in byte _) =>
                commands.ClearBuffer(
                    commands.GetBuffer(state.Buffer),
                    new BufferRange(0, CopySize),
                    state.Value));
    }

    private static void AddWorkerClearPass(
        RenderGraphEdit edit,
        Queue queue,
        string label,
        GraphBufferId buffer,
        uint value)
    {
        _ = edit.AddCopyPass<ClearState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            new ClearState(buffer, value),
            new PassOptions(
                Culling: PassCullingMode.NeverCull,
                EstimatedRecordingCost: 8),
            static (ref PassDefinition access, ref ClearState state) =>
                _ = access.Write(
                    state.Buffer,
                    new BufferRange(0, CopySize),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in ClearState state, in byte _) =>
                commands.ClearBuffer(
                    commands.GetBuffer(state.Buffer),
                    new BufferRange(0, CopySize),
                    state.Value));
    }

    private static BufferBoundaryState Endpoint(Buffer buffer, ResourceContentState contents) => new(
        new BufferRange(0, buffer.Info.Size),
        buffer.InitialSync,
        buffer.InitialAccess,
        contents);

    private static string FormatAllocations(ReadOnlySpan<int> frames, ReadOnlySpan<long> bytes)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < frames.Length; i++)
        {
            if (i != 0) builder.Append(", ");
            builder.Append(frames[i]).Append(':').Append(bytes[i]);
        }
        return builder.ToString();
    }

    private readonly record struct CopyState(
        GraphBufferId Source,
        GraphBufferId Destination);

    private readonly record struct ClearState(GraphBufferId Buffer, uint Value);

    private readonly record struct ClearTextureState(
        GraphTextureId Texture,
        TextureSubresourceRange Range);
}
