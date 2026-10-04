using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CountedBufferRangesTests
{
    [Theory]
    [InlineData(BufferContentScope.CountBoundedRanges, true)]
    [InlineData(BufferContentScope.CountBoundedPrefix, false)]
    [InlineData(BufferContentScope.FullRange, false)]
    public void DisjointRangesRequireReadersToRespectTheirBounds(BufferContentScope scope, bool accepted)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer upload = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
        using (MappedBuffer mapped = support.Backend.Map(upload, MapType.Write, BufferRange.Whole))
            expected.CopyTo(mapped.Bytes);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.RegisterExternalBuffer(upload,
                [new(BufferRange.Whole, upload.InitialSync, upload.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferId result = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId ranges = edit.CreateTransientBuffer(new BufferDesc(32,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddCopyFramePass("write two bounded ranges", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: source, Destination: ranges), default,
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Partial, resultContents: ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 4, 4));
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 4, commands.GetBuffer(data.Destination), 24, 4));
                });
            edit.AddCopyFramePass("read only the produced elements", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: ranges, Destination: result, Scope: scope), default,
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination, BufferContentScope Scope) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource, contentScope: data.Scope);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination, BufferContentScope Scope) data) =>
                {
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 4, commands.GetBuffer(data.Destination), 0, 4));
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 24, commands.GetBuffer(data.Destination), 4, 4));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        InvalidOperationException? failure = null;
        try { frame.Execute(completions); }
        catch (InvalidOperationException error) { failure = error; }
        if (!accepted)
        {
            Assert.IsType<InvalidOperationException>(failure);
            return;
        }
        Assert.Null(failure);
        support.Wait(completions);
        using MappedBuffer readback = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
        Assert.True(readback.Bytes.SequenceEqual(expected));
    }
}
