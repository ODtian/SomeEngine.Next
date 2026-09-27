using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class BufferDataCommandsTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void BufferDataAndCommandEditsReachEveryFrame(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: false);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId source, destination;
        GraphBufferInput input;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            source = edit.DeclareExternalBuffer(new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
            destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync, result.InitialAccess,
                    ResourceContentState.Undefined)]);
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            pass = edit.AddCopyFramePass("copy changing data", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new CopyData(input, destination, 0), new PassOptions(RepeatCommands: true),
                static (ref PassDefinition definition, ref CopyData data) =>
                {
                    definition.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    definition.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data) =>
                {
                    BufferRegion input = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new BufferCopy(input.Buffer, input.Range.Offset + data.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 16; index++)
        {
            if (index == 8)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.SetBufferDescription(source, new BufferDesc(96, BufferUsages.CopySource), MemoryType.Upload);
                edit.Commit();
            }
            byte[] bytes = Enumerable.Range(0, index < 8 ? 64 : 96)
                .Select(value => unchecked((byte)(index * 13 + value))).ToArray();
            ulong offset = index < 5 ? 0u : 16u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode)))
            {
                frame.SetBufferData(source, bytes);
                frame.SetBufferInput(input, new GraphBufferSlice(source, BufferRange.Whole));
                frame.SetPassDataIfChanged(pass, new CopyData(input, destination, offset));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.True(mapping.Bytes.SequenceEqual(bytes.AsSpan((int)offset, 32)));
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(pass);
            edit.Remove(source);
            edit.Commit();
        }
        using RenderGraphFrame last = graph.BeginFrame();
        last.Execute(completions);
        support.Wait(completions);
    }

    private readonly record struct CopyData(GraphBufferInput Source, GraphBufferId Destination, ulong Offset);
}
