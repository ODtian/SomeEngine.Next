using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class FrameIdentityTests
{
    [Fact]
    public void PassDefinitionCannotModifyAnotherFrameAfterReuse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphBufferId buffer;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopyDestination));
            pass = edit.DeclareCopyPass("write", PassQueueSelection.Exact(support.GraphicsQueue));
            edit.Commit();
        }
        RenderGraphFrame first = graph.BeginFrame();
        PassDefinition definition = first.GetPass(pass);
        first.Dispose();
        using RenderGraphFrame next = graph.BeginFrame();
        bool rejected = false;
        try
        {
            definition.Write(buffer, new BufferRange(0, 64), PipelineSync.Copy,
                ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected);
    }

#if DEBUG
    [Fact]
    public void FrameBufferRangesRejectOutOfBoundsValuesInDevelopment()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId buffer;
        GraphBufferCbvId view;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.DeclareExternalBuffer(new BufferDesc(
                256,
                BufferUsages.Constant));
            view = edit.CreateBufferCbv(buffer, new BufferRange(0, 64));
            edit.Commit();
        }

        using RenderGraphFrame frame = graph.BeginFrame();
        frame.SetBufferRange(buffer, new BufferRange(64, 64));
        frame.SetBufferRange(view, new BufferRange(128, 64));
        bool bufferRejected = false;
        try { frame.SetBufferRange(buffer, new BufferRange(240, 32)); }
        catch (InvalidOperationException) { bufferRejected = true; }
        bool viewRejected = false;
        try { frame.SetBufferRange(view, new BufferRange(240, 32)); }
        catch (InvalidOperationException) { viewRejected = true; }
        Assert.True(bufferRejected);
        Assert.True(viewRejected);
    }
#endif

    [Fact]
    public void FrameLocalIdentityDoesNotCollideWithGraphIdentity_DoesNotThrow()
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
}
