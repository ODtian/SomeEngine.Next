using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ComputeQueueTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void ChangingCompatibleComputeToGraphicsPreservesEarlierFrames(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId intermediate = edit.CreateBuffer(new(32,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("produce on compute", PassQueueSelection.AnyOfType(QueueType.Compute),
                (input, intermediate), default,
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Buffer), 0, 32));
                });
            consumer = edit.AddComputeFramePass("compatible compute commands", PassQueueSelection.AnyCompatible(),
                (intermediate, destination), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref ComputePassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 32)));
            edit.Commit();
        }

        Queue? selected = null;
        var options = new RenderGraphFrameOptions(SubmissionMode: mode, Diagnostics: CaptureQueue);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(options);
            Execute(frame, iteration, support.ComputeQueue);
        }

        using (RenderGraphFrame earlier = graph.BeginFrame(options))
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                edit.SetPassQueue(consumer, PassQueueSelection.AnyOfType(QueueType.Graphics));
                edit.Commit();
            }
            Execute(earlier, 4, support.ComputeQueue);
        }
        for (int iteration = 5; iteration < 10; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(options);
            Execute(frame, iteration, support.GraphicsQueue);
        }

        ulong version = graph.StructureVersion;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassQueue(consumer, PassQueueSelection.AnyOfType(QueueType.Graphics));
            edit.Commit();
        }
        Assert.Equal(version, graph.StructureVersion);

        void Execute(RenderGraphFrame frame, int iteration, Queue expectedQueue)
        {
            selected = null;
            byte[] expected = Enumerable.Range(0, 32).Select(index => (byte)(index * 7 + iteration)).ToArray();
            frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            Assert.Same(expectedQueue, selected);
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }

        void CaptureQueue(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphPassDiagnostic pass in diagnostics.Passes)
                if (pass.Id == consumer) selected = pass.Queue;
        }
    }
}
