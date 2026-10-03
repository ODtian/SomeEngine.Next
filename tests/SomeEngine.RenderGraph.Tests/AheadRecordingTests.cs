namespace SomeEngine.RenderGraph.Tests;

public sealed class AheadRecordingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingFailurePreservesEarlierSubmissionsAndAllowsNextFrame(bool disableParallel)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        bool fail = true;
        int calls = 0;
        GraphPassId first;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = edit.DeclareCopyPass("earlier copy", PassQueueSelection.AnyOfType(QueueType.Copy), options: new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread));
            for (int index = 0; index < 4; index++)
            {
                GraphPassId later = edit.AddCopyFramePass(
                    $"later copy {index}", PassQueueSelection.AnyOfType(QueueType.Graphics), index,
                    new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedRecordingCost: 64),
                    static (ref PassDefinition _, ref int _) => { },
                    (ref CopyPassCommandScope _, in int number) =>
                    {
                        Interlocked.Increment(ref calls);
                        if (number == 2 && fail) throw new InvalidOperationException("recording failed");
                    });
                edit.OrderAfter(later, first);
            }
            edit.Commit();
        }

        var options = new RenderGraphFrameOptions(FrameSubmissionMode.RecordAhead,
            disableParallel ? RenderGraphDebugOptions.DisableParallelRecording : default);
        var receiver = new ResultReceiver(first);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(options))
        {
            frame.ObserveExecution(receiver);
            bool failed = false;
            try { frame.Execute(completions); }
            catch (Exception exception)
            {
                Assert.Contains("recording failed", exception.ToString());
                failed = true;
            }
            Assert.True(failed);
        }
        Assert.True(receiver.FirstSubmitted);
        support.Wait(receiver.Completions);
        int before = Volatile.Read(ref calls);
        fail = false;
        using (RenderGraphFrame frame = graph.BeginFrame(options))
        {
            int count = frame.Execute(completions);
            Assert.Equal(2, count);
            support.Wait(completions.AsSpan(0, count));
        }
        Assert.Equal(before + 4, Volatile.Read(ref calls));
    }

    private sealed class ResultReceiver(GraphPassId first) : IRenderGraphFrameExecutionObserver
    {
        internal bool FirstSubmitted;
        internal QueueCompletion[] Completions = [];

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            FirstSubmitted = result.TryGetPassCompletion(first, out _);
            Completions = result.SubmittedCompletions.ToArray();
        }
    }
}
