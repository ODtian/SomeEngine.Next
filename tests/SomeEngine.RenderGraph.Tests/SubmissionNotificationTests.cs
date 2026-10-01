using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class SubmissionNotificationTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void EarlyCopyCanBeReadBeforeLaterRecordingFailsAndAfterGraphEdits(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId first;
        bool fail = true, laterRecorded = false;
        var options = new PassOptions(Culling: PassCullingMode.NeverCull,
            Recording: PassRecordingMode.CallingThread) { SubmitAfter = true };
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            first = edit.AddCopyFramePass("early output", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: input, Destination: destination), options,
                static (ref PassDefinition access, ref (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    access.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    access.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            GraphPassId later = edit.AddGeneralFramePass("later work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                0, options with { SubmitAfter = false },
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) =>
                {
                    laterRecorded = true;
                    if (fail) throw new InvalidOperationException("later recording failed");
                });
            edit.OrderAfter(later, first);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            bool early = iteration % 2 == 0;
            if (iteration < 2)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.SetPassOptions(first, options with { SubmitAfter = early });
                edit.Commit();
            }
            fail = early;
            laterRecorded = false;
            byte[] expected = Enumerable.Range(0, 32).Select(value => (byte)(value + iteration * 23)).ToArray();
            bool observedBeforeLater = false;
            var observer = new Receiver(result =>
            {
                if (!result.TryGetPassCompletion(first, out QueueCompletion completion)) return;
                observedBeforeLater = !laterRecorded;
                support.Wait([completion]);
                using MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                Assert.Equal(expected, mapping.Bytes.ToArray());
            });
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
            {
                if (iteration >= 2) frame.SetPassOptions(first, options with { SubmitAfter = early });
                frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
                frame.ObserveExecution(observer);
                if (fail)
                {
                    bool threw = false;
                    try { frame.Execute(completions); }
                    catch (Exception exception)
                    {
                        Assert.Contains("later recording failed", exception.ToString());
                        threw = true;
                    }
                    Assert.True(threw);
                }
                else frame.Execute(completions);
            }
            Assert.Equal(early, observedBeforeLater);
        }
    }

    private delegate void SubmissionReceived(RenderGraphFrameResult result);
    private sealed class Receiver(SubmissionReceived receive) : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => receive(result);
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) { }
    }
}
