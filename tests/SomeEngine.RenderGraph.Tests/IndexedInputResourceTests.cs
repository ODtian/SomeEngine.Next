using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class IndexedInputResourceTests
{
    [Fact]
    public void RemovingAnUnusedResourcePreservesOtherIndexedReads()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer unused = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.ShaderRead));
        using Buffer source = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.ShaderRead | BufferUsages.CopySource), MemoryType.Upload);
        using Buffer replacement = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.ShaderRead));
        using (MappedBuffer bytes = support.Backend.Map(source, MapType.Write, BufferRange.Whole))
        {
            new byte[] { 42, 0, 0, 0 }.CopyTo(bytes.Bytes);
            bytes.Flush(bytes.Range);
        }
        using BufferSrv sourceView = support.Backend.CreateBufferSrv(support.Device,
            new BufferSrvDesc(source, BufferRange.Whole));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId unusedId;
        GraphParameterInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync,
                    result.InitialAccess, ResourceContentState.Undefined)]);
            unusedId = edit.RegisterExternalBuffer(unused,
                [new BufferBoundaryState(BufferRange.Whole, unused.InitialSync,
                    unused.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [new BufferBoundaryState(BufferRange.Whole, source.InitialSync,
                    source.InitialAccess, ResourceContentState.Defined)]);
            input = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            edit.AddCopyFramePass("copy the indexed material input",
                PassQueueSelection.Exact(support.GraphicsQueue), (sourceId, destination), default,
                (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(input, PipelineSync.ComputeShading);
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, static (ref CopyPassCommandScope commands,
                    in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 4)));
            edit.Commit();
        }
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPersistentParameterInput(input, ResourceBinding.ReadOnlyBuffer(sourceView));
            frame.Execute(submitted);
            support.Wait(submitted);
        }
        using (MappedBuffer bytes = support.Backend.Map(result, MapType.Read, BufferRange.Whole))
        {
            bytes.Invalidate(bytes.Range);
            Assert.Equal(new byte[] { 42, 0, 0, 0 }, bytes.Bytes.ToArray());
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(unusedId);
            edit.RegisterExternalBuffer(replacement,
                [new BufferBoundaryState(BufferRange.Whole, replacement.InitialSync,
                    replacement.InitialAccess, ResourceContentState.Undefined)]);
            edit.Commit();
        }
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.Execute(submitted);
            support.Wait(submitted);
        }
        using (MappedBuffer bytes = support.Backend.Map(result, MapType.Read, BufferRange.Whole))
        {
            bytes.Invalidate(bytes.Range);
            Assert.Equal(new byte[] { 42, 0, 0, 0 }, bytes.Bytes.ToArray());
        }
    }
}
