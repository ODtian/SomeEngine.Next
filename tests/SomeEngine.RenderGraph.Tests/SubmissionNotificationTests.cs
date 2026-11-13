namespace SomeEngine.RenderGraph.Tests;

public sealed class SubmissionNotificationTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void SubmissionObserversRunAfterEveryPassHasBeenRecorded(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var events = new List<int>();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            var options = new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread);
            GraphPassId first = edit.AddCopyFramePass("first", PassQueueSelection.OnQueue(support.GraphicsQueue), 0,
                options with { SubmitAfter = true }, static (ref PassDefinition _, ref int _) => { },
                (ref CopyPassCommandScope _, in int _) => events.Add(1));
            GraphPassId second = edit.AddCopyFramePass("second", PassQueueSelection.OnQueue(support.GraphicsQueue), 0,
                options, static (ref PassDefinition _, ref int _) => { },
                (ref CopyPassCommandScope _, in int _) => events.Add(2));
            edit.OrderAfter(second, first);
            edit.Commit();
        }
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.ObserveExecution(new Receiver(events));
            frame.Execute();
        }
        Assert.Equal(new[] { 1, 2, 3 }, events);
    }
    private sealed class Receiver(List<int> events) : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission) => events.Add(3);
    }
}
