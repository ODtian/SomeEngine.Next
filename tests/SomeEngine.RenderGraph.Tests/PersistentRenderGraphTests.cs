using SomeEngine.Graphics;
using SomeEngine.RenderGraph.Diagnostics;
using System.Numerics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PersistentRenderGraphTests
{
    private const int ByteCount = 256;

    [Fact]
    public void EditCommitIsAtomicAndInvalidatesRemovedIds()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        GraphBufferId first;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.Commit();
        }
        ulong version = graph.StructureVersion;

        using (RenderGraphEdit abandoned = graph.BeginEdit())
            _ = abandoned.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopySource));
        Assert.Equal(version, graph.StructureVersion);

        GraphBufferId replacement;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(first);
            replacement = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.Commit();
        }
        Assert.NotEqual(first, replacement);
        Assert.Equal(version + 1, graph.StructureVersion);

        using RenderGraphEdit stale = graph.BeginEdit();
        bool rejected = false;
        try
        {
            stale.Remove(first);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Assert.True(rejected, "The removed GraphBufferId generation must be rejected.");
    }

    [Fact]
    public void PersistentGraphExecutesTwoCrossQueueFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer upload = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = BuildCopyGraph(support, upload, readback);

        ExecuteAndVerify(support, graph, upload, readback, 17);
        ExecuteAndVerify(support, graph, upload, readback, 91);
        Assert.Equal(1UL, graph.StructureVersion);
    }

    [Fact]
    public void DeclaredExternalSlotsBindPerFrameResources()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId sourceSlot;
        GraphBufferId destinationSlot;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopySource),
                MemoryType.Upload);
            destinationSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopyDestination),
                MemoryType.Readback);
            _ = edit.AddCopyPass<CopyState, byte>(
                "external slots",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(sourceSlot, destinationSlot, ByteCount),
                default,
                DeclareCopy,
                static (ref CopyPassCommandScope commands, in CopyState copy, in byte _) =>
                    RecordCopy(ref commands, copy));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalBuffer(sourceSlot, source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            frame.BindExternalBuffer(destinationSlot, destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void DynamicBufferAccessIdsDriveTheRecordedRange()
    {
        const ulong offset = 16;
        const ulong size = 32;
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphPassId pass;
        GraphBufferAccessId sourceAccess = default;
        GraphBufferAccessId destinationAccess = default;
        CopyState copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            copy = new CopyState(sourceId, destinationId, size);
            pass = edit.AddCopyPass<CopyState, DynamicCopyFrame>(
                "dynamic range copy",
                PassQueueSelection.Exact(support.GraphicsQueue),
                copy,
                default,
                (ref PassDefinition definition, ref CopyState state) =>
                {
                    sourceAccess = definition.Read(
                        state.Source,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource,
                        dynamicRange: true);
                    destinationAccess = definition.Write(
                        state.Destination,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in CopyState definition,
                    in DynamicCopyFrame frame) =>
                {
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(definition.Source),
                        frame.Offset,
                        commands.GetBuffer(definition.Destination),
                        frame.Offset,
                        frame.Size));
                });
            edit.Commit();
        }

        byte[] expected = new byte[checked((int)ByteCount)];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 17 + 3));
        using (MappedBuffer mapped = support.Backend.Map(
            source,
            MapType.Write,
            new BufferRange(0, ByteCount)))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, ByteCount));
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicCopyFrame(offset, size));
            frame.SetBufferRange(sourceAccess, new BufferRange(offset, size));
            frame.SetBufferRange(destinationAccess, new BufferRange(offset, size));
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);

        byte[] actual = new byte[checked((int)size)];
        using (MappedBuffer mapped = support.Backend.Map(
            destination,
            MapType.Read,
            new BufferRange(offset, size)))
        {
            mapped.Invalidate(new BufferRange(offset, size));
            mapped.Bytes.CopyTo(actual);
        }
        Assert.Equal(
            expected.AsSpan(checked((int)offset), checked((int)size)).ToArray(),
            actual);
    }

    [Fact]
    public void DynamicTextureAccessIdsDriveTheRecordedSubresource()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        GraphTextureAccessId textureAccess = default;
        GraphPassId pass;
        TextureWriteState state;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                width: 16,
                height: 16,
                depth: 1,
                mipLevelCount: 3,
                arrayLayerCount: 1,
                sampleCount: 1,
                format: Format.R8G8B8A8UNorm,
                usages: TextureUsages.ColorAttachment));
            state = new TextureWriteState(texture);
            pass = edit.AddCopyPass<TextureWriteState, DynamicTextureWriteFrame>(
                "dynamic texture range",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition definition, ref TextureWriteState definitionState) =>
                {
                    textureAccess = definition.Write(
                        definitionState.Texture,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget,
                        TextureLayout.RenderTarget,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in TextureWriteState definitionState,
                    in DynamicTextureWriteFrame frame) =>
                {
                    Texture texture = commands.GetTexture(definitionState.Texture);
                    Assert.True(
                        texture.Info.Usages.HasFlag(TextureUsages.ColorAttachment),
                        $"Expected ColorAttachment usage, actual: {texture.Info.Usages}.");
                    commands.ClearTexture(
                        texture,
                        frame.Range,
                        new Vector4(0.25f, 0.5f, 0.75f, 1.0f));
                });
            edit.Commit();
        }

        TextureSubresourceRange range = new(1, 1, 0, 1, TextureAspects.Color);
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicTextureWriteFrame(range));
            frame.SetTextureRange(textureAccess, range);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void CompleteOverwriteCullsDeadProducerAndReportsPlacedAliasing()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.CopyQueue, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphBufferId scratch = edit.CreateTransientBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));

            _ = AddCopy(edit, "Dead write", support.CopyQueue,
                new CopyState(sourceId, scratch, ByteCount));
            _ = AddCopy(edit, "Live overwrite", support.CopyQueue,
                new CopyState(sourceId, scratch, ByteCount));
            _ = AddCopy(edit, "Observable readback", support.GraphicsQueue,
                new CopyState(scratch, destinationId, ByteCount));
            edit.Commit();
        }

        RenderGraphSnapshot? snapshot = null;
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(
                   new RenderGraphFrameOptions(Diagnostics: Capture)))
        {
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Passes, pass => pass.Label == "Dead write" && !pass.Live);
        Assert.Contains(snapshot.Passes, pass => pass.Label == "Live overwrite" && pass.Live);
        Assert.True(snapshot.Statistics.PhysicalTransientBytes <=
                    snapshot.Statistics.LogicalTransientBytes);

        void Capture(in RenderGraphDiagnosticsView view) =>
            snapshot = CaptureSnapshot(view);
    }

    [Fact]
    public void FrameExtensionParticipatesInCullingAndScheduling()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        GraphExtensionPointId point;
        GraphBufferId sourceId;
        GraphBufferId destinationId;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.CopyQueue, ResourceContentState.Defined)]);
            destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            point = edit.AddExtensionPoint("Dynamic work");
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            using RenderGraphFrameExtension extension = frame.BeginExtension(point);
            GraphBufferId transient = extension.CreateBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = extension.AddCopyPass(
                "Extension upload",
                PassQueueSelection.Exact(support.CopyQueue),
                new CopyState(sourceId, transient, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = extension.AddCopyPass(
                "Extension readback",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(transient, destinationId, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
    }

    [Fact]
    public void FrameLocalIdentityDoesNotCollideWithGraphIdentity()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using RenderGraphFrame frame = graph.BeginFrame();
        GraphBufferId buffer = frame.CreateBuffer(new BufferDesc(
            64,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = frame.CreateBufferSrv(buffer, new BufferRange(0, 64));
    }

    [Fact]
    public void StaleFrameCopyCannotOperateOnReusedFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        RenderGraphFrame first = graph.BeginFrame();
        RenderGraphFrame stale = first;
        Assert.Equal(0, first.Execute(completions));

        RenderGraphFrame second = graph.BeginFrame();
        bool rejected = false;
        try
        {
            _ = stale.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A copied frame lease must not revive when its FrameSlot is reused.");

        stale.Dispose();
        _ = second.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        second.Dispose();
    }

    [Fact]
    public void StaleFrameExtensionCannotOperateOnReusedFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphExtensionPointId point;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            point = edit.AddExtensionPoint("dynamic");
            edit.Commit();
        }
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        RenderGraphFrame first = graph.BeginFrame();
        RenderGraphFrameExtension extension = first.BeginExtension(point);
        RenderGraphFrameExtension stale = extension;
        Assert.Equal(0, first.Execute(completions));

        RenderGraphFrame second = graph.BeginFrame();
        using RenderGraphFrameExtension current = second.BeginExtension(point);
        bool rejected = false;
        try
        {
            _ = stale.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A copied frame-extension lease must not revive with a new frame.");

        stale.Dispose();
        _ = current.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        second.Dispose();
    }

    [Fact]
    public void CopiedCommandContextLeaseReturnsPoolEntryOnlyOnce()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var pool = new CommandContextPool(support.Backend, support.Device);

        CommandContextPool.CommandContextLease original =
            pool.Acquire(support.GraphicsQueue, bundle: false, "lease copy test");
        CommandContextPool.CommandContextLease duplicate = original;
        CommandContext context = original.Context;
        original.Dispose();
        duplicate.Dispose();

        using CommandContextPool.CommandContextLease first =
            pool.Acquire(support.GraphicsQueue, bundle: false, "first reacquire");
        using CommandContextPool.CommandContextLease second =
            pool.Acquire(support.GraphicsQueue, bundle: false, "second reacquire");

        Assert.Same(context, first.Context);
        Assert.NotSame(first.Context, second.Context);
    }

    [Fact]
    public void ExplicitUndefinedResultRejectsSameFrameRead()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddCopyPass<int, byte>(
                "discard contents",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                default,
                (ref PassDefinition access, ref int state) =>
                    _ = access.Write(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Undefined),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            _ = edit.AddCopyPass<int, byte>(
                "read discarded contents",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref int state) =>
                    _ = access.Read(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        InvalidOperationException? exception = null;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException value) { exception = value; }
        Assert.NotNull(exception);
        Assert.Contains("undefined Buffer contents", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteOnlyDeclarationCannotAuthorizeReadCommand()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            _ = edit.AddCopyPass<CopyState, byte>(
                "invalid read direction",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(sourceId, destinationId, 64),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref CopyState state) =>
                {
                    _ = access.Write(state.Source, new BufferRange(0, state.Size),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                    _ = access.Write(state.Destination, new BufferRange(0, state.Size),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyState state, in byte _) =>
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(state.Source), 0,
                        commands.GetBuffer(state.Destination), 0,
                        state.Size)));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        InvalidOperationException? exception = null;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException value) { exception = value; }
        Assert.NotNull(exception);
        Assert.Contains("not covered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialSubmissionTransfersAcceptedCompletionsToFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddCopyPass<int, byte>(
                "accepted first wave",
                PassQueueSelection.Exact(support.CopyQueue),
                0,
                default,
                (ref PassDefinition access, ref int state) =>
                    _ = access.Write(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            _ = edit.AddCopyPass<int, byte>(
                "failing second wave",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref int state) =>
                    _ = access.Read(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in int _, in byte _) =>
                    throw new InvalidOperationException("injected callback failure"));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        bool failed = false;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException) { failed = true; }
        Assert.True(failed);
        Assert.True(graph.InFlightCompletionCount > 0,
            "A successfully submitted wave must remain attached to the FrameSlot after a later failure.");
    }

    [Fact]
    public void ExecuteCompactsCompletionsForNonContiguousUsedQueueSlots()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId graphicsBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            GraphBufferId copyBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            AddClear(edit, "graphics clear", support.GraphicsQueue, graphicsBuffer, 1);
            AddClear(edit, "copy clear", support.CopyQueue, copyBuffer, 2);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count;
        using (RenderGraphFrame frame = graph.BeginFrame())
            count = frame.Execute(completions);

        Assert.Equal(2, count);
        Assert.Same(support.GraphicsQueue, completions[0].Queue);
        Assert.Same(support.CopyQueue, completions[1].Queue);
        support.Wait(completions.AsSpan(0, count));
    }

    [Fact]
    public void PersistentPassDataIsClearedWhenItsFrameSlotIsReused()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] observed = [0];
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddCopyPass<ObservationState, int>(
                "frame data reset",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ObservationState(observed),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref ObservationState _) => { },
                static (ref CopyPassCommandScope _, in ObservationState state, in int frameValue) =>
                    state.Destination[0] = frameValue);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, 17);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(17, observed[0]);

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(0, observed[0]);
    }

    [Fact]
    public void GeneralPassRequiresAGraphicsQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        _ = edit.AddGeneralPass<int, byte>(
            "General on Copy",
            PassQueueSelection.Exact(support.CopyQueue),
            0,
            default,
            static (ref PassDefinition _, ref int _) => { },
            static (ref GeneralPassCommandScope _, in int _, in byte _) => { });
        bool rejected = false;
        try
        {
            edit.Commit();
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A general pass must not be accepted on a Copy Queue.");
    }

    [Fact]
    public void FrameGeneralPassRejectsCopyQueueDuringAuthoring()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphFrame frame = graph.BeginFrame();

        bool rejected = false;
        try
        {
            _ = frame.AddGeneralPass(
                "General on Copy",
                PassQueueSelection.Exact(support.CopyQueue),
                0,
                default,
                static (ref PassDefinition _, ref int _) => { },
                static (ref GeneralPassCommandScope _, in int _) => { });
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A frame-local general pass must reject a Copy Queue during authoring.");
    }

    private static RenderGraph BuildCopyGraph(
        WarpGraphTestSupport support,
        Buffer upload,
        Buffer readback)
    {
        var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        GraphBufferId uploadId = edit.RegisterExternalBuffer(upload,
            [Endpoint(upload, support.CopyQueue, ResourceContentState.Defined)]);
        GraphBufferId readbackId = edit.RegisterExternalBuffer(readback,
            [Endpoint(readback, support.GraphicsQueue, ResourceContentState.Undefined)]);
        GraphBufferId transient = edit.CreateTransientBuffer(new BufferDesc(
            ByteCount,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = AddCopy(edit, "Upload", support.CopyQueue,
            new CopyState(uploadId, transient, ByteCount));
        _ = AddCopy(edit, "Readback", support.GraphicsQueue,
            new CopyState(transient, readbackId, ByteCount));
        edit.Commit();
        return graph;
    }

    private static GraphPassId AddCopy(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        in CopyState state) =>
        edit.AddCopyPass<CopyState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            state,
            default,
            DeclareCopy,
            static (ref CopyPassCommandScope commands, in CopyState copy, in byte _) =>
                RecordCopy(ref commands, copy));

    private static void AddClear(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        GraphBufferId buffer,
        uint value)
    {
        _ = edit.AddCopyPass<ClearState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            new ClearState(buffer, value),
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ClearState state) =>
                _ = access.Write(
                    state.Buffer,
                    new BufferRange(0, 64),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in ClearState state, in byte _) =>
                commands.ClearBuffer(
                    commands.GetBuffer(state.Buffer),
                    new BufferRange(0, 64),
                    state.Value));
    }

    private static void DeclareCopy(ref PassDefinition access, ref CopyState state)
    {
        _ = access.Read(state.Source, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopySource);
        _ = access.Write(state.Destination, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }

    private static void RecordCopy(ref CopyPassCommandScope commands, in CopyState state)
    {
        Buffer source = commands.GetBuffer(state.Source);
        Buffer destination = commands.GetBuffer(state.Destination);
        commands.CopyBuffer(new BufferCopy(source, 0, destination, 0, state.Size));
    }

    private static void ExecuteAndVerify(
        WarpGraphTestSupport support,
        RenderGraph graph,
        Buffer upload,
        Buffer readback,
        byte seed)
    {
        byte[] expected = new byte[ByteCount];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 31 + seed));
        BufferRange range = new(0, ByteCount);
        using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            _ = frame.Execute(completions);
        support.Wait(completions);

        byte[] actual = new byte[ByteCount];
        using (MappedBuffer mapping = support.Backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);
    }

    private static BufferBoundaryState Endpoint(
        Buffer buffer,
        Queue queue,
        ResourceContentState contents) =>
        new(new BufferRange(0, buffer.Info.Size),
            buffer.InitialSync, buffer.InitialAccess, contents, queue);

    private static RenderGraphSnapshot CaptureSnapshot(in RenderGraphDiagnosticsView view)
    {
        return RenderGraphSnapshot.Capture(view);
    }

    private readonly record struct CopyState(
        GraphBufferId Source,
        GraphBufferId Destination,
        ulong Size);

    private readonly record struct ClearState(GraphBufferId Buffer, uint Value);

    private readonly record struct ObservationState(int[] Destination);

    private readonly record struct DynamicCopyFrame(ulong Offset, ulong Size);

    private readonly record struct TextureWriteState(GraphTextureId Texture);

    private readonly record struct DynamicTextureWriteFrame(TextureSubresourceRange Range);
}
