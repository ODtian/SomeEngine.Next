using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameAnalysisReuseTests
{
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
        string? failure = null;
        try { invalid.Execute(completions); }
        catch (InvalidOperationException exception) { failure = exception.Message; }
        Assert.Contains("RG4001", failure);
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
