using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RepeatedCommandSequenceTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.Pipelined)]
    public void ReusedCommandsObserveLocalDataChangesAndSurroundingWrites(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        const int count = 48;
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(count * sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId destination;
        var passes = new GraphPassId[count];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            for (int index = 0; index < count; index++)
            {
                var value = new BufferWord(destination, (ulong)index * sizeof(uint), (uint)index + 1);
                passes[index] = edit.AddCopyFramePass("write output element", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    value, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: index != 0 && index != count - 1),
                    static (ref PassDefinition pass, ref BufferWord data) => pass.Write(data.Buffer,
                        new BufferRange(data.Offset, sizeof(uint)), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in BufferWord data) =>
                        commands.ClearBuffer(commands.GetBuffer(data.Buffer), new BufferRange(data.Offset, sizeof(uint)), data.Value));
                if (index != 0) edit.OrderAfter(passes[index], passes[index - 1]);
            }
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            uint changed = frameIndex < 4 ? 18u : frameIndex < 8 ? 1001u : 2002u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode)))
            {
                frame.SetPassDataIfChanged(passes[17], new BufferWord(destination, 17 * sizeof(uint), changed));
                frame.SetPassData(passes[0], new BufferWord(destination, 0, (uint)frameIndex + 100));
                frame.SetPassData(passes[^1], new BufferWord(destination, (count - 1) * sizeof(uint), (uint)frameIndex + 200));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer readback = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            readback.Invalidate(readback.Range);
            ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(readback.Bytes);
            for (int index = 0; index < count; index++)
            {
                uint expected = index == 0 ? (uint)frameIndex + 100 : index == count - 1 ? (uint)frameIndex + 200 :
                    index == 17 ? changed : (uint)index + 1;
                Assert.Equal(expected, values[index]);
            }
        }
    }

    private readonly record struct BufferWord(GraphBufferId Buffer, ulong Offset, uint Value);
}
