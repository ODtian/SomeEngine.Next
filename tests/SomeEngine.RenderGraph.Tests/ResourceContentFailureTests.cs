using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class ResourceContentFailureTests
{
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
            _ = edit.DeclareCopyPass("discard contents", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) =>
                    _ = access.Write(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Undefined));
            _ = edit.DeclareCopyPass("read discarded contents", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) =>
                    _ = access.Read(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource), options: new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        bool rejected = false;
        try { _ = frame.Execute(completions); }
        catch (UndefinedResourceReadException) { rejected = true; }
        Assert.True(rejected);
    }

    [Fact]
    public void WriteOnlyDeclarationMismatchIsCheckedOnlyInDevelopment()
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
#if DEBUG
        Assert.NotNull(exception);
#else
        Assert.Null(exception);
#endif
    }
}
