using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameAnalysisReuseTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void UploadedBytesRemainIndependentWhileAllFramesAreInFlight(int frameCount)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)frameCount));
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc((ulong)frameCount * 16, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId copy;
        GraphBufferId destination;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            copy = edit.AddCopyFramePass("copy in-flight bytes", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new BufferedCopyData(input, destination, 0),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref BufferedCopyData data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in BufferedCopyData data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), data.DestinationOffset, 16));
                });
            edit.Commit();
        }
        byte[] expected = Enumerable.Range(0, frameCount * 16).Select(value => (byte)(value + 17)).ToArray();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            for (int index = 0; index < frameCount; index++)
            {
                using RenderGraphFrame frame = graph.BeginFrame();
                frame.SetPassData(copy, new BufferedCopyData(input, destination, (ulong)index * 16));
                frame.SetBufferInput(input, frame.Upload(expected.AsSpan(index * 16, 16), BufferUsages.CopySource));
                frame.Execute(completions);
            }
            bool available = graph.TryBeginFrame(out RenderGraphFrame extra);
            extra.Dispose();
            Assert.False(available);
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        using (MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }
        for (int index = 0; index < 16; index++) expected[index] ^= 0xC3;
        using RenderGraphFrame reused = graph.BeginFrame();
        reused.SetPassData(copy, new BufferedCopyData(input, destination, 0));
        reused.SetBufferInput(input, reused.Upload(expected.AsSpan(0, 16), BufferUsages.CopySource));
        reused.Execute(completions);
        support.Wait(completions);
        using MappedBuffer updated = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
        updated.Invalidate(updated.Range);
        Assert.Equal(expected, updated.Bytes.ToArray());
    }

    private readonly record struct BufferedCopyData(
        GraphBufferInput Source, GraphBufferId Destination, ulong DestinationOffset);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RetainedInputViewsFollowUploadsAndReboundBuffers(int framesInFlight)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
        BufferDesc description = new(16, BufferUsages.ShaderRead | BufferUsages.CopySource);
        using Buffer first = support.Backend.CreateBuffer(support.Device, description, MemoryType.Upload);
        using Buffer second = support.Backend.CreateBuffer(support.Device, description, MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId sourceId;
        GraphBufferInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            input = edit.CreateBufferInput(description.Usages);
            GraphBufferSrvId view = edit.CreateBufferSrv(input, structureStride: 4);
            GraphBufferId target = edit.RegisterExternalBuffer(destination,
                [new BufferBoundaryState(new BufferRange(0, 4), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
            var data = new InputCopyData(input, view, target);
            edit.AddGeneralFramePass("copy through retained input view", PassQueueSelection.Exact(support.GraphicsQueue),
                data, default, InputCopyData.Declare, InputCopyData.Record);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 24; index++)
        {
            Buffer source = index / 6 % 2 == 0 ? first : second;
            byte[] bytes = Enumerable.Range(0, 16).Select(value => (byte)(index * 7 + value)).ToArray();
            using (MappedBuffer mapping = support.Backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                bytes.CopyTo(mapping.Bytes);
                mapping.Flush(new BufferRange(0, 16));
            }
            ulong offset = (ulong)(index / 3 % 4 * 4);
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.BindExternalBuffer(sourceId, source,
                    [new BufferBoundaryState(new BufferRange(0, 16), source.InitialSync,
                        source.InitialAccess, ResourceContentState.Defined)]);
                GraphBufferSlice value = index < 12
                    ? frame.Upload(bytes, description.Usages)
                    : new GraphBufferSlice(sourceId, BufferRange.Whole);
                frame.SetBufferInput(input,
                    new GraphBufferSlice(value.Buffer, new BufferRange(value.Range.Offset + offset, 4)));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer result = support.Backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(new BufferRange(0, 4));
            Assert.True(result.Bytes.SequenceEqual(bytes.AsSpan((int)offset, 4)));
        }
    }

    private readonly record struct InputCopyData(GraphBufferInput Input, GraphBufferSrvId View, GraphBufferId Destination)
    {
        internal static void Declare(ref PassDefinition pass, ref InputCopyData data)
        {
            pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Read(data.View, PipelineSync.ComputeShading);
            pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref GeneralPassCommandScope commands, in InputCopyData data)
        {
            BufferSrv view = commands.GetBufferSrv(data.View);
            commands.CopyBuffer(new BufferCopy(view.Description.Buffer, view.Description.Range.Offset,
                commands.GetBuffer(data.Destination), 0, 4));
        }
    }

    [Fact]
    public void DynamicUploadsReuseAnalysisAndRefreshResourcesAndCopyRanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        BufferDesc description = new(16, BufferUsages.CopyDestination, "changing readback");
        using Buffer first = support.Backend.CreateBuffer(support.Device, description, MemoryType.Readback);
        using Buffer second = support.Backend.CreateBuffer(support.Device, description, MemoryType.Readback);
        GraphBufferId destination;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            destination = edit.DeclareExternalBuffer(description, MemoryType.Readback);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool reused = false;
        for (int index = 0; index < 8; index++)
        {
            Buffer target = index % 2 == 0 ? first : second;
            byte[] bytes = Enumerable.Range(0, 8).Select(value => (byte)(index * 16 + value)).ToArray();
            ulong size = index < 3 ? 4u : 8u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Capture)))
            {
                frame.BindExternalBuffer(destination, target,
                    [new BufferBoundaryState(new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
                _ = frame.Upload(new byte[4], BufferUsages.ShaderRead);
                GraphBufferSlice source = frame.Upload(bytes, BufferUsages.ShaderRead | BufferUsages.CopySource, "changing upload");
                GraphBufferSrvId view = frame.CreateBufferSrv(source);
                var data = new CopyData(source, destination, size);
                frame.AddCopyPass("copy changing frame data",
                    PassQueueSelection.Exact(support.GraphicsQueue), data, default,
                    (ref PassDefinition pass, ref CopyData copyData) =>
                    {
                        CopyData.Declare(ref pass, ref copyData);
                        pass.Read(view, PipelineSync.ComputeShading);
                    }, CopyData.Record);
                frame.Execute(completions);
                support.Wait(completions);
            }
            Assert.Equal(index is not (0 or 3), reused);
            using MappedBuffer mapping = support.Backend.Map(target, MapType.Read, new BufferRange(0, size));
            Assert.True(mapping.Bytes.SequenceEqual(bytes.AsSpan(0, checked((int)size))));
        }
        void Capture(in RenderGraphDiagnosticsView diagnostics) => reused = diagnostics.Statistics.AnalysisReused;
    }

    [Fact]
    public void ChangedExternalContentsAreValidatedAfterAnalysisWasReused()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        BufferDesc sourceDescription = new(16, BufferUsages.CopySource, "content source");
        using Buffer source = support.Backend.CreateBuffer(support.Device, sourceDescription, MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId sourceId;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.DeclareExternalBuffer(sourceDescription, MemoryType.Upload);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [new BufferBoundaryState(new BufferRange(0, 16), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("copy validated contents", PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyData(new GraphBufferSlice(sourceId, new BufferRange(0, 16)), destinationId, 16), default, CopyData.Declare, CopyData.Record);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool reused = false;
        for (int index = 0; index < 3; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Capture));
            frame.BindExternalBuffer(sourceId, source,
                [new BufferBoundaryState(new BufferRange(0, 16),
                    index == 0 ? source.InitialSync : PipelineSync.Copy,
                    index == 0 ? source.InitialAccess : ResourceAccess.CopySource,
                    ResourceContentState.Defined)]);
            frame.Execute(completions);
            support.Wait(completions);
        }
        Assert.True(reused);
        using RenderGraphFrame invalid = graph.BeginFrame();
        invalid.BindExternalBuffer(sourceId, source,
            [new BufferBoundaryState(new BufferRange(0, 16), source.InitialSync,
                source.InitialAccess, ResourceContentState.Undefined)]);
        InvalidOperationException? failure = null;
        try { invalid.Execute(completions); }
        catch (InvalidOperationException exception) { failure = exception; }
        Assert.IsType<InvalidOperationException>(failure);
        void Capture(in RenderGraphDiagnosticsView diagnostics) => reused = diagnostics.Statistics.AnalysisReused;
    }

    private readonly record struct CopyData(GraphBufferSlice Source, GraphBufferId Destination, ulong Size)
    {
        internal static void Declare(ref PassDefinition pass, ref CopyData data)
        {
            pass.Read(data.Source.Buffer, new BufferRange(data.Source.Range.Offset, data.Size), PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Write(data.Destination, new BufferRange(0, data.Size), PipelineSync.Copy,
                ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref CopyPassCommandScope commands, in CopyData data) =>
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
                commands.GetBuffer(data.Destination), 0, data.Size));
    }
}
