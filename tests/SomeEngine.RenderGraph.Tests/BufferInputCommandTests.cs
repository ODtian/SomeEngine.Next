using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class BufferInputCommandTests
{
    [Fact]
    public void ChangingAnInputSliceSizeUpdatesCommandsThatReadItsDeclaredPrefix()
    {
        // The direct backend exercises command reuse; the validation wrapper declines it.
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer source = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId sourceId;
        GraphBufferInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.RegisterExternalBuffer(source,
                [new(BufferRange.Whole, source.InitialSync, source.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferId outputId = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphPassId clear = edit.AddCopyFramePass("Clear output", PassQueueSelection.AnyOfType(QueueType.Graphics),
                outputId, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref GraphBufferId data) =>
                    pass.Write(data, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphBufferId data) =>
                    commands.ClearBuffer(commands.GetBuffer(data), BufferRange.Whole, 0));
            GraphPassId copy = edit.AddCopyFramePass("Copy input prefix", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (input, outputId), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new BufferRange(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    BufferRegion region = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new BufferCopy(region.Buffer, region.Range.Offset,
                        commands.GetBuffer(data.Output), region.Range.Size == 16 ? 0UL : 4UL, 4));
                });
            edit.OrderAfter(copy, clear);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 5; index++)
        {
            uint value = checked((uint)index + 11);
            using (MappedBuffer mapped = support.Backend.Map(source, MapType.Write, new BufferRange(0, 4)))
            {
                MemoryMarshal.Cast<byte, uint>(mapped.Bytes)[0] = value;
                mapped.Flush(mapped.Range);
            }
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: FrameSubmissionMode.RecordAhead)))
            {
                frame.SetBufferInput(input, new GraphBufferSlice(sourceId, new BufferRange(0, index < 3 ? 16UL : 32UL)));
                frame.Execute(completions);
            }
            support.Wait(completions);
            using MappedBuffer result = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            uint[] expected = index < 3 ? [value, 0] : [0, value];
            Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
        }
    }
}
