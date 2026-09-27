using System.Buffers.Binary;

namespace SomeEngine.RenderGraph.Tests;

public sealed class LargeAccessOrderTests
{
    [Fact]
    public void ManySharedBufferAccessesFollowExplicitOrderAcrossFrames()
    {
        const int count = 96;
        using var support = WarpGraphTestSupport.Create(enableValidation: true);
        using var source = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(count * 4, BufferUsages.CopySource), MemoryType.Upload);
        using var destination = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using (var bytes = support.Backend.Map(source, MapType.Write, BufferRange.Whole))
        {
            for (int index = 0; index < count; index++) BinaryPrimitives.WriteInt32LittleEndian(bytes.Bytes.Slice(index * 4, 4), index + 1);
            bytes.Flush(bytes.Range);
        }
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (var edit = graph.BeginEdit())
        {
            var sourceId = edit.RegisterExternalBuffer(source,
                [new(BufferRange.Whole, source.InitialSync, source.InitialAccess, ResourceContentState.Defined)]);
            var destinationId = edit.RegisterExternalBuffer(destination,
                [new(BufferRange.Whole, destination.InitialSync, destination.InitialAccess, ResourceContentState.Undefined)]);
            var passes = new GraphPassId[count];
            for (int index = 0; index < count; index++)
            {
                var data = (Source: sourceId, Destination: destinationId, Offset: (ulong)index * 4);
                passes[index] = edit.AddCopyFramePass("shared destination copy", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    data, new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination, ulong Offset) value) =>
                    {
                        pass.Read(value.Source, new BufferRange(value.Offset, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                        pass.Write(value.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination, ulong Offset) value) =>
                        commands.CopyBuffer(new(commands.GetBuffer(value.Source), value.Offset, commands.GetBuffer(value.Destination), 0, 4)));
            }
            for (int index = 1; index < count; index++) edit.OrderAfter(passes[index - 1], passes[index]);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            using var frame = graph.BeginFrame();
            frame.Execute(completions);
            foreach (var completion in completions)
                if (completion != default) Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
            using var bytes = support.Backend.Map(destination, MapType.Read, BufferRange.Whole);
            bytes.Invalidate(bytes.Range);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.Bytes));
        }
    }
}
