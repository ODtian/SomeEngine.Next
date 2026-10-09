using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameBufferRangeViewTests
{
    [Fact]
    public void WholeFrameRangesKeepEachDescriptorsSubview()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
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
            GraphBufferSrvId first = edit.CreateBufferSrv(source, new BufferRange(4, 4), structureStride: sizeof(uint));
            GraphBufferSrvId second = edit.CreateBufferSrv(source, new BufferRange(32, 4), structureStride: sizeof(uint));
            edit.AddCopyFramePass("read the first descriptor subview",
                PassQueueSelection.Exact(support.GraphicsQueue), first,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId view) =>
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(view)),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId view) => { });
            edit.AddCopyFramePass("read the second descriptor subview",
                PassQueueSelection.Exact(support.ComputeQueue), second,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId view) =>
                    pass.Bind(view, PipelineSync.ComputeShading),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId view) => { });
            edit.Commit();
        }

        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRanges(source, new BufferRangeSet([BufferRange.Whole]));
            frame.Execute(submitted);
            support.Wait(submitted);
        }

        Assert.Equal(1, graph.GetRangeCompletionCount(buffer, new BufferRange(4, 4)));
        Assert.Equal(1, graph.GetRangeCompletionCount(buffer, new BufferRange(32, 4)));
        Assert.Equal(2, graph.GetRangeCompletionCount(buffer, BufferRange.Whole));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(0, 4)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(8, 24)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(36, 28)));
    }

    [Fact]
    public void WholeAndExplicitFrameRangesKeepBothDescriptorUses()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer buffer = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.ShaderRead), MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue]);
        GraphBufferId source;
        GraphBufferSrvId first, second;
        GraphRegionId region;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            source = edit.RegisterExternalBuffer(buffer,
                [new BufferBoundaryState(BufferRange.Whole, buffer.InitialSync,
                    buffer.InitialAccess, ResourceContentState.Defined)]);
            first = edit.CreateBufferSrv(source, new BufferRange(4, 40), structureStride: sizeof(uint));
            second = edit.CreateBufferSrv(source, new BufferRange(32, 28), structureStride: sizeof(uint));
            region = edit.AddFrameRegion("descriptor readers");
            edit.Commit();
        }

        var ranges = new BufferRangeSet([BufferRange.Whole, new BufferRange(36, 4)]);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRanges(source, ranges);
            frame.AddCopyPass("read the first descriptor and the shared range",
                PassQueueSelection.Exact(support.GraphicsQueue), first,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId view) =>
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(view)),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId view) => { }, region: region);
            frame.AddCopyPass("read the second descriptor and the shared range",
                PassQueueSelection.Exact(support.ComputeQueue), second,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferSrvId view) =>
                    pass.Bind(view, PipelineSync.ComputeShading),
                static (ref CopyPassCommandScope commands, in GraphBufferSrvId view) => { }, region: region);
            frame.Execute(submitted);
            support.Wait(submitted);
        }

        Assert.Equal(1, graph.GetRangeCompletionCount(buffer, new BufferRange(4, 28)));
        Assert.Equal(2, graph.GetRangeCompletionCount(buffer, new BufferRange(32, 12)));
        Assert.Equal(1, graph.GetRangeCompletionCount(buffer, new BufferRange(44, 16)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(0, 4)));
        Assert.Equal(0, graph.GetRangeCompletionCount(buffer, new BufferRange(60, 4)));
    }
}
