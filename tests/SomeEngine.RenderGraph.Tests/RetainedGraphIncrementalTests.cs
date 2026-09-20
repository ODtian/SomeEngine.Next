using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

#if DEBUG
public sealed class RetainedGraphIncrementalTests
{


    [Fact]
    public void OldFrameCompletesAgainstOldBlocksAfterNewVersionCommits()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        Branch branch;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            branch = AddBranch(edit, support.GraphicsQueue, "overlap");
            edit.Commit();
        }

        using RenderGraphFrame oldFrame = graph.BeginFrame();
        ulong oldVersion = oldFrame.StructureVersion;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassLabel(branch.ReadPass, "new-version reader");
            edit.SetPassOptions(branch.ReadPass, new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 3));
            edit.Commit();
        }
        Assert.Equal(oldVersion + 1, graph.StructureVersion);

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Assert.Equal(1, oldFrame.Execute(completions));
        support.Wait(completions);

        completions.AsSpan().Clear();
        using (RenderGraphFrame newFrame = graph.BeginFrame())
        {
            Assert.Equal(graph.StructureVersion, newFrame.StructureVersion);
            Assert.Equal(1, newFrame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void BufferRangeCompletionReturnsAtMostOneMaximumPerQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId target = edit.RegisterExternalBuffer(
                buffer,
                [new BufferBoundaryState(
                    new BufferRange(0, 64),
                    buffer.InitialSync,
                    buffer.InitialAccess,
                    ResourceContentState.Undefined)]);
            var state = new DeclarationState(target);
            _ = edit.AddCopyPass<DeclarationState, byte>(
                "range completion writer",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref DeclarationState data) =>
                    data.Access = pass.Write(
                        data.Buffer,
                        new BufferRange(0, 32),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in DeclarationState _, in byte _) => { });
            edit.Commit();
        }

        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(submitted));

        Assert.Equal(1, graph.GetRangeCompletionCount(buffer, new BufferRange(0, 16)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(32, 32)));
        var copied = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Assert.Equal(1, graph.CopyRangeCompletions(buffer, new BufferRange(0, 32), copied));
        Assert.Same(support.GraphicsQueue, copied[0].Queue);
        Assert.Equal(submitted[0].Value, copied[0].Value);
        support.Wait(submitted);
    }



    [Fact]
    public void LocalTransientResizeKeepsUnchangedCachedRhiBuffer()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var leftCapture = new BackingCapture();
        var rightCapture = new BackingCapture();
        GraphBufferId left;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            left = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination,
                "cached-left"));
            GraphBufferId right = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination,
                "cached-right"));
            AddBackingCapture(edit, support.GraphicsQueue, left, leftCapture, "capture-left");
            AddBackingCapture(edit, support.GraphicsQueue, right, rightCapture, "capture-right");
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 4; frameIndex++)
        {
            completions.AsSpan().Clear();
            using (RenderGraphFrame frame = graph.BeginFrame())
                Assert.Equal(1, frame.Execute(completions));
            support.Wait(completions);
        }
        Buffer leftBefore = Assert.IsAssignableFrom<Buffer>(leftCapture.Last);
        Buffer rightBefore = Assert.IsAssignableFrom<Buffer>(rightCapture.Last);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetBufferDescription(
                left,
                new BufferDesc(
                    128,
                    BufferUsages.CopyDestination,
                    "cached-left resized"));
            edit.Commit();
        }

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions);

        Assert.NotSame(leftBefore, leftCapture.Last);
        Assert.Same(rightBefore, rightCapture.Last);

        Buffer leftAfterResize = Assert.IsAssignableFrom<Buffer>(leftCapture.Last);
        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions);
        Assert.Same(leftAfterResize, leftCapture.Last);
        Assert.Same(rightBefore, rightCapture.Last);
    }

    [Fact]
    public void CompactionPreservesStructureVersionAndGraphExecution()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        Branch branch;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            branch = AddBranch(edit, support.GraphicsQueue, "compact");
            edit.Commit();
        }
        ulong version = graph.StructureVersion;
        graph.CompactTransientResources();
        Assert.Equal(version, graph.StructureVersion);

        var completions = new QueueCompletion[3];
        int completionCount;
        using (RenderGraphFrame frame = graph.BeginFrame())
            completionCount = frame.Execute(completions);
        Assert.Equal(1, completionCount);
        support.Wait(completions.AsSpan(0, completionCount));

    }



    private static Branch AddBranch(RenderGraphEdit edit, Queue queue, string label)
    {
        GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
            64,
            BufferUsages.CopySource | BufferUsages.CopyDestination,
            label));
        var write = new DeclarationState(buffer);
        GraphPassId writePass = edit.AddCopyPass<DeclarationState, byte>(
            $"{label} write",
            PassQueueSelection.Exact(queue),
            write,
            default,
            static (ref PassDefinition pass, ref DeclarationState state) =>
                state.Access = pass.Write(
                    state.Buffer,
                    new BufferRange(0, 64),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope _, in DeclarationState _, in byte _) => { });
        var read = new DeclarationState(buffer);
        GraphPassId readPass = edit.AddCopyPass<DeclarationState, byte>(
            $"{label} read",
            PassQueueSelection.Exact(queue),
            read,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref DeclarationState state) =>
                state.Access = pass.Read(
                    state.Buffer,
                    new BufferRange(0, 64),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource),
            static (ref CopyPassCommandScope _, in DeclarationState _, in byte _) => { });
        return new Branch(
            buffer,
            writePass,
            readPass,
            write.Access,
            read.Access);
    }

    private static void AddBackingCapture(
        RenderGraphEdit edit,
        Queue queue,
        GraphBufferId buffer,
        BackingCapture capture,
        string label)
    {
        var state = new BackingCaptureState(buffer, capture);
        _ = edit.AddCopyPass<BackingCaptureState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            state,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref BackingCaptureState value) =>
                _ = pass.Write(
                    value.Buffer,
                    BufferRange.Whole,
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in BackingCaptureState value, in byte _) =>
                value.Capture.Last = commands.GetBuffer(value.Buffer));
    }

    private sealed class DeclarationState(GraphBufferId buffer)
    {
        internal GraphBufferId Buffer { get; } = buffer;
        internal GraphBufferAccessId Access { get; set; }
    }

    private sealed class ResidentReadState(GraphBufferId buffer, GraphBufferSrvId view)
    {
        internal GraphBufferId Buffer { get; } = buffer;
        internal GraphBufferSrvId View { get; } = view;
        internal GraphBufferAccessId Access { get; set; }
    }

    private sealed class BackingCapture
    {
        internal Buffer? Last { get; set; }
    }

    private sealed class BackingCaptureState(GraphBufferId buffer, BackingCapture capture)
    {
        internal GraphBufferId Buffer { get; } = buffer;
        internal BackingCapture Capture { get; } = capture;
    }

    private readonly record struct Branch(
        GraphBufferId Buffer,
        GraphPassId WritePass,
        GraphPassId ReadPass,
        GraphBufferAccessId WriteAccess,
        GraphBufferAccessId ReadAccess);
}
#endif
