using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameBufferRangesTests
{
    [Fact]
    public void DescriptorReadersReportCompletionOnlyForTheFramesBufferRanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.ShaderRead), MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue]);
        GraphBufferId source;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            source = edit.RegisterExternalBuffer(buffer,
                [new BufferBoundaryState(BufferRange.Whole, buffer.InitialSync,
                    buffer.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferSrvId view = edit.CreateBufferSrv(source);
            edit.AddCopyFramePass("read resident bytes through a descriptor",
                PassQueueSelection.Exact(support.GraphicsQueue), view,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId data) =>
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(data)),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId data) => { });
            edit.AddCopyFramePass("read resident bytes through an ordinary binding",
                PassQueueSelection.Exact(support.ComputeQueue), view,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId data) =>
                    pass.Bind(data, PipelineSync.ComputeShading),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId data) => { });
            edit.Commit();
        }

        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRanges(source, new BufferRangeSet([new(8, 8), new(32, 8)]));
            frame.Execute(submitted);
        }

        var used = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Assert.Equal(2, graph.CopyRangeCompletions(buffer, new BufferRange(8, 8), used));
        Assert.All(submitted, completion => Assert.Contains(completion, used));
        Assert.Equal(2, graph.GetRangeCompletionCount(buffer, new BufferRange(32, 8)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(0, 8)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(16, 16)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(40, 24)));
        support.Wait(submitted);
    }

    [Fact]
    public void ChangingBufferRangesUpdatesTheRetainedDescriptorsCurrentUse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.ShaderRead), MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId source;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            source = edit.RegisterExternalBuffer(buffer,
                [new BufferBoundaryState(BufferRange.Whole, buffer.InitialSync,
                    buffer.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferSrvId view = edit.CreateBufferSrv(source);
            edit.AddCopyFramePass("read the frames accessible bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                view, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId data) =>
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(data)),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId data) => { });
            edit.Commit();
        }
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRanges(source, new BufferRangeSet([new(4, 4)]));
            frame.Execute(submitted);
            support.Wait(submitted);
        }
        ulong version = graph.StructureVersion;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRanges(source, new BufferRangeSet([new(32, 4)]));
            frame.Execute(submitted);
            support.Wait(submitted);
        }
        var used = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Assert.Equal(1, graph.CopyRangeCompletions(buffer, new BufferRange(32, 4), used));
        Assert.Equal(submitted[0], used[0]);
        int previousCount = graph.CopyRangeCompletions(buffer, new BufferRange(4, 4), used);
        Assert.DoesNotContain(submitted[0], used.Take(previousCount));
        Assert.Equal(version, graph.StructureVersion);
    }

    [Fact]
    public void OneParameterInputOrdersEveryConsumerAfterTheFramesUpload()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer source = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using BufferSrv sourceView = support.Backend.CreateBufferSrv(support.Device,
            new BufferSrvDesc(source, BufferRange.Whole));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphBufferId sourceId;
        GraphParameterInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.RegisterExternalBuffer(source,
                [new BufferBoundaryState(BufferRange.Whole, source.InitialSync,
                    source.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync,
                    result.InitialAccess, ResourceContentState.Undefined)]);
            input = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            for (ulong offset = 0; offset < 8; offset += 4)
                edit.AddCopyFramePass("copy bytes selected by a material input",
                    PassQueueSelection.Exact(support.GraphicsQueue), (sourceId, destination, offset), default,
                    (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination, ulong Offset) data) =>
                    {
                        pass.Bind(input, PipelineSync.ComputeShading);
                        pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                        pass.Write(data.Destination, new BufferRange(data.Offset, 4), PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    }, static (ref CopyPassCommandScope commands,
                        in (GraphBufferId Source, GraphBufferId Destination, ulong Offset) data) =>
                        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                            commands.GetBuffer(data.Destination), data.Offset, 4)));
            edit.Commit();
        }

        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            GraphBufferSlice upload = frame.Upload([42, 0, 0, 0], BufferUsages.CopySource);
            GraphPassId writer = frame.AddCopyPass("upload material bytes",
                PassQueueSelection.Exact(support.GraphicsQueue), (upload, sourceId), default,
                static (ref PassDefinition pass, ref (GraphBufferSlice Upload, GraphBufferId Target) data) =>
                {
                    pass.Read(data.Upload.Buffer, data.Upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Target, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, static (ref CopyPassCommandScope commands,
                    in (GraphBufferSlice Upload, GraphBufferId Target) data) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Upload.Buffer), data.Upload.Range.Offset,
                        commands.GetBuffer(data.Target), 0, 4)));
            frame.SetParameterInput(input, ResourceBinding.ReadOnlyBuffer(sourceView));
            frame.OrderAfter(input, writer);
            frame.Execute(submitted);
            support.Wait(submitted);
        }

        using MappedBuffer bytes = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
        Assert.Equal(new byte[] { 42, 0, 0, 0, 42, 0, 0, 0 }, bytes.Bytes.ToArray());
    }

    [Fact]
    public void GrowingFrameCopyWorkKeepsEveryCopyResultAcrossFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(20, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId destination;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddRegion("retained work position");
            destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync,
                    result.InitialAccess, ResourceContentState.Undefined)]);
            edit.Commit();
        }
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        foreach (int count in new[] { 3, 5 })
        {
            byte[] values = Enumerable.Range(1, count * 4).Select(static value => (byte)value).ToArray();
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                GraphBufferSlice upload = frame.Upload(values, BufferUsages.CopySource);
                for (ulong offset = 0; offset < (ulong)values.Length; offset += 4)
                    frame.AddCopyPass("copy a frame value", PassQueueSelection.Exact(support.GraphicsQueue),
                        (upload, destination, offset), default,
                        static (ref PassDefinition pass,
                            ref (GraphBufferSlice Upload, GraphBufferId Destination, ulong Offset) data) =>
                        {
                            pass.Read(data.Upload.Buffer, new BufferRange(data.Upload.Range.Offset + data.Offset, 4),
                                PipelineSync.Copy, ResourceAccess.CopySource);
                            pass.Write(data.Destination, new BufferRange(data.Offset, 4), PipelineSync.Copy,
                                ResourceAccess.CopyDestination, WriteCoverage.Complete);
                        }, static (ref CopyPassCommandScope commands,
                            in (GraphBufferSlice Upload, GraphBufferId Destination, ulong Offset) data) =>
                            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Upload.Buffer),
                                data.Upload.Range.Offset + data.Offset, commands.GetBuffer(data.Destination), data.Offset, 4)));
                frame.Execute(submitted);
                support.Wait(submitted);
            }
            using MappedBuffer bytes = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            Assert.Equal(values, bytes.Bytes[..values.Length].ToArray());
        }
    }
}
