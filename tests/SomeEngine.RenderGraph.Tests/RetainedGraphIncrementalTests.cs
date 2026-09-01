using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

#if DEBUG
public sealed class RetainedGraphIncrementalTests
{
    [Fact]
    public void StructuralEditsPreserveUnrelatedBlocksHazardsIdsAndPlacements()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        Branch left;
        Branch right;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            left = AddBranch(edit, support.GraphicsQueue, "left");
            right = AddBranch(edit, support.GraphicsQueue, "right");
            edit.Commit();
        }

        GraphStructureIndex initial = graph.StructureIndex;
        object initialBufferBlock = initial.Structure.Buffers.BlockIdentity;
        object initialAccessBlock = initial.Structure.Accesses.BlockIdentity;
        GraphHazardTimeline rightHazards = initial.HazardTimelines[right.Buffer.Value];
        GraphIdentity[] rightAccesses = initial.ResourceAccesses[right.Buffer.Value];
        GraphTransientPlacementState rightPlacement = initial.TransientPlacements[right.Buffer.Value];

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassOptions(
                left.ReadPass,
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    EstimatedExecutionCost: 3));
            edit.Commit();
        }

        GraphStructureIndex passEdit = graph.StructureIndex;
        Assert.Same(initialBufferBlock, passEdit.Structure.Buffers.BlockIdentity);
        Assert.Same(initialAccessBlock, passEdit.Structure.Accesses.BlockIdentity);
        Assert.NotSame(initial.Structure.Passes.BlockIdentity, passEdit.Structure.Passes.BlockIdentity);
        Assert.Same(rightHazards, passEdit.HazardTimelines[right.Buffer.Value]);
        Assert.Same(rightAccesses, passEdit.ResourceAccesses[right.Buffer.Value]);
        Assert.Equal(rightPlacement, passEdit.TransientPlacements[right.Buffer.Value]);
        Assert.Equal(1, graph.LastEditStatistics.EditedPassCount);
        Assert.Equal(0, graph.LastEditStatistics.ResourcesReanalyzed);
        Assert.Equal(1, graph.LastEditStatistics.CopiedStructureBlockCount);
        Assert.Equal(9, graph.LastEditStatistics.SharedStructureBlockCount);
        Assert.InRange(graph.LastEditStatistics.ScheduleNodesRevisited, 1, 2);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetBufferAccess(
                left.WriteAccess,
                GraphAccessMode.Write,
                WriteCoverage.Complete,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                new BufferRange(0, 64),
                resultContents: ResourceContentState.Defined);
            edit.Commit();
        }

        GraphStructureIndex accessEdit = graph.StructureIndex;
        Assert.Same(rightHazards, accessEdit.HazardTimelines[right.Buffer.Value]);
        Assert.Same(rightAccesses, accessEdit.ResourceAccesses[right.Buffer.Value]);
        Assert.Equal(rightPlacement, accessEdit.TransientPlacements[right.Buffer.Value]);
        Assert.Equal(0, graph.LastEditStatistics.EditedPassCount);
        Assert.Equal(1, graph.LastEditStatistics.ResourcesReanalyzed);
        Assert.Equal(1, graph.LastEditStatistics.CopiedStructureBlockCount);
        Assert.Equal(1, graph.LastEditStatistics.HazardEdgesRebuilt);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(right.WritePass, left.ReadPass);
            edit.Commit();
        }

        GraphStructureIndex orderEdit = graph.StructureIndex;
        Assert.Same(rightHazards, orderEdit.HazardTimelines[right.Buffer.Value]);
        Assert.Equal(rightPlacement, orderEdit.TransientPlacements[right.Buffer.Value]);
        Assert.Equal(0, graph.LastEditStatistics.ResourcesReanalyzed);
        Assert.Equal(1, graph.LastEditStatistics.CopiedStructureBlockCount);
        Assert.InRange(graph.LastEditStatistics.ScheduleNodesRevisited, 2, 3);

        ulong leftPlacementToken = orderEdit.TransientPlacements[left.Buffer.Value].Token;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetBufferDescription(
                left.Buffer,
                new BufferDesc(
                    128,
                    BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "resized left"));
            edit.Commit();
        }

        GraphStructureIndex resizeEdit = graph.StructureIndex;
        Assert.NotEqual(leftPlacementToken, resizeEdit.TransientPlacements[left.Buffer.Value].Token);
        Assert.Equal(rightPlacement, resizeEdit.TransientPlacements[right.Buffer.Value]);
        Assert.Same(rightHazards, resizeEdit.HazardTimelines[right.Buffer.Value]);
        Assert.Equal(1, graph.LastEditStatistics.ResourcesReanalyzed);
        Assert.Equal(1, graph.LastEditStatistics.TransientAllocationsMoved);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(left.ReadPass);
            edit.Remove(left.WritePass);
            edit.Remove(left.Buffer);
            edit.Commit();
        }

        GraphStructureIndex deleteEdit = graph.StructureIndex;
        Assert.DoesNotContain(left.Buffer.Value, deleteEdit.BufferIds);
        Assert.Contains(right.Buffer.Value, deleteEdit.BufferIds);
        Assert.Contains(right.WritePass.Value, deleteEdit.PassIds);
        Assert.Contains(right.ReadPass.Value, deleteEdit.PassIds);
        Assert.Same(rightHazards, deleteEdit.HazardTimelines[right.Buffer.Value]);
        Assert.Equal(
            rightPlacement.Token,
            deleteEdit.TransientPlacements[right.Buffer.Value].Token);
        Assert.Equal(1, graph.LastEditStatistics.ResourcesReanalyzed);
        Assert.Equal(0, graph.LastEditStatistics.TransientAllocationsMoved);
    }

    [Fact]
    public void ExplicitOrderPlacesNewPassBeforeExistingHazardConsumer()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        Branch branch;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            branch = AddBranch(edit, support.GraphicsQueue, "ordered insertion");
            edit.Commit();
        }

        GraphPassId middle;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            var state = new DeclarationState(branch.Buffer);
            middle = edit.AddCopyPass<DeclarationState, byte>(
                "ordered middle write",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                default,
                static (ref PassDefinition pass, ref DeclarationState value) =>
                    value.Access = pass.Write(
                        value.Buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in DeclarationState _, in byte _) => { });
            edit.OrderAfter(middle, branch.WritePass);
            edit.OrderAfter(branch.ReadPass, middle);
            edit.Commit();
        }

        GraphHazardTimeline hazards = graph.StructureIndex.HazardTimelines[branch.Buffer.Value];
        Assert.Contains(
            hazards.Edges,
            edge => edge.Predecessor == middle.Value && edge.Consumer == branch.ReadPass.Value);
        Assert.DoesNotContain(
            hazards.Edges,
            edge => edge.Predecessor == branch.ReadPass.Value && edge.Consumer == middle.Value);
    }

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
    public void DescriptorOnlySrvTracksExplicitResidentRangesWithoutOrderingFreshUploads()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer pageHeap = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(
                64,
                BufferUsages.ShaderRead | BufferUsages.CopyDestination,
                "range-tracked page heap"),
            MemoryType.DeviceLocal);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        GraphBufferId pageHeapId;
        GraphPassId reader;
        GraphPassId upload;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pageHeapId = edit.RegisterExternalBuffer(
                pageHeap,
                [new BufferBoundaryState(
                    new BufferRange(0, 64),
                    pageHeap.InitialSync,
                    pageHeap.InitialAccess,
                    ResourceContentState.Defined)]);
            GraphBufferSrvId wholeHeap = edit.CreateBufferSrv(pageHeapId, BufferRange.Whole);
            var readState = new ResidentReadState(pageHeapId, wholeHeap);
            reader = edit.AddComputePass<ResidentReadState, byte>(
                "read old resident range",
                PassQueueSelection.Exact(support.GraphicsQueue),
                readState,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref ResidentReadState state) =>
                {
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(state.View));
                    state.Access = pass.Read(
                        state.Buffer,
                        new BufferRange(0, 32),
                        PipelineSync.ComputeShading,
                        ResourceAccess.ShaderResource);
                },
                static (ref ComputePassCommandScope _, in ResidentReadState _, in byte _) => { });
            var uploadState = new DeclarationState(pageHeapId);
            upload = edit.AddCopyPass<DeclarationState, byte>(
                "upload fresh unpublished range",
                PassQueueSelection.Exact(support.CopyQueue),
                uploadState,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref DeclarationState state) =>
                    state.Access = pass.Write(
                        state.Buffer,
                        new BufferRange(32, 32),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in DeclarationState _, in byte _) => { });
            edit.Commit();
        }

        GraphStructureIndex index = graph.StructureIndex;
        GraphIdentity[] accesses = index.ResourceAccesses[pageHeapId.Value];
        Assert.Equal(2, accesses.Length);
        Assert.Contains(accesses, id =>
            index.Structure.Accesses.Get(id).BufferRange == new BufferRange(0, 32));
        Assert.Contains(accesses, id =>
            index.Structure.Accesses.Get(id).BufferRange == new BufferRange(32, 32));
        Assert.Empty(index.HazardTimelines[pageHeapId.Value].Edges);
        Assert.DoesNotContain(reader.Value, index.DependencyPredecessors.GetValueOrDefault(upload.Value, []));
        Assert.DoesNotContain(upload.Value, index.DependencyPredecessors.GetValueOrDefault(reader.Value, []));
    }

    [Fact]
    public void UnchangedTransientKeepsPhysicalHeapOffsetAcrossLocalResize()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Branch left;
        Branch right;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            left = AddBranch(edit, support.GraphicsQueue, "physical-left");
            right = AddBranch(edit, support.GraphicsQueue, "physical-right");
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions);
        Assert.True(graph.TryGetTransientPlacement(right.Buffer, out Heap? heap, out ulong offset));

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetBufferDescription(
                left.Buffer,
                new BufferDesc(
                    128,
                    BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "physical-left resized"));
            edit.Commit();
        }

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions);
        Assert.True(graph.TryGetTransientPlacement(
            right.Buffer,
            out Heap? retainedHeap,
            out ulong retainedOffset));
        Assert.Same(heap, retainedHeap);
        Assert.Equal(offset, retainedOffset);
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
    public void ExplicitIdleCompactChangesOnlyTransientPlacementTokens()
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
        GraphStructureIndex before = graph.StructureIndex;
        ulong token = before.TransientPlacements[branch.Buffer.Value].Token;
        ulong version = graph.StructureVersion;

        graph.CompactTransientResources();

        Assert.Equal(version + 1, graph.StructureVersion);
        Assert.NotEqual(token, graph.StructureIndex.TransientPlacements[branch.Buffer.Value].Token);
        Assert.Same(before.Structure, graph.StructureIndex.Structure);
        Assert.Equal(1, graph.LastEditStatistics.TransientAllocationsMoved);
        Assert.Equal(0, graph.LastEditStatistics.CopiedStructureBlockCount);
    }

    [Fact]
    public void ExtensionPointAndFollowingPassReceiveDistinctStableOrdinals()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        GraphExtensionPointId point;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            point = edit.AddExtensionPoint("one-shot work");
            pass = edit.AddCopyPass<int, byte>(
                "retained work",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            edit.Commit();
        }

        GraphStructure structure = graph.StructureIndex.Structure;
        int extensionOrdinal = structure.ExtensionPoints.Get(point.Value).DeclarationOrdinal;
        int passOrdinal = structure.Passes.Get(pass.Value).DeclarationOrdinal;
        Assert.Equal(extensionOrdinal + 1, passOrdinal);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassLabel(pass, "renamed retained work");
            edit.Commit();
        }
        Assert.Equal(
            passOrdinal,
            graph.StructureIndex.Structure.Passes.Get(pass.Value).DeclarationOrdinal);
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
