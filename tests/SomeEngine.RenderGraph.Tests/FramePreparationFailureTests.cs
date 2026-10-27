using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class FramePreparationFailureTests
{
    [Fact]
    public void PreparationFailureDoesNotReportEarlierSubmissions()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame first = graph.BeginFrame())
        {
            _ = first.DeclareGeneralPass("Submitted pass", PassQueueSelection.AnyOfType(QueueType.Graphics),
                options: new PassOptions(Culling: PassCullingMode.NeverCull));
            Assert.True(first.Execute(completions) > 0);
        }
        support.Wait(completions);

        var observer = new FailingPreparationObserver();
        Exception? failure = null;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.ObserveExecution(observer);
            try { frame.Execute(completions); }
            catch (Exception exception) { failure = exception; }
        }

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, observer.FinishedCount);
        Assert.Empty(observer.SubmittedCompletions);
    }

    private sealed class FailingPreparationObserver : IRenderGraphFrameExecutionObserver
    {
        public int FinishedCount { get; private set; }
        public QueueCompletion[] SubmittedCompletions { get; private set; } = [];

        public void OnRenderGraphFramePreparing(ref RenderGraphFrame frame) =>
            throw new InvalidOperationException();

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            FinishedCount++;
            SubmittedCompletions = result.SubmittedCompletions.ToArray();
        }
    }
}
