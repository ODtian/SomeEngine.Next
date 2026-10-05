using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class FrameCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionCallbacksReportSubmissionAndReleaseFrameAfterCallbackFailure(bool execute)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        RenderGraphFrame frame = graph.BeginFrame();
        GraphPassId pass = frame.DeclareGeneralPass("Observed pass", PassQueueSelection.AnyOfType(QueueType.Graphics), options: new PassOptions(Culling: PassCullingMode.NeverCull));
        var result = new CompletionReceiver(pass);
        frame.ObserveExecution(new ThrowingCompletionReceiver());
        frame.ObserveExecution(result);
        bool failed = false;
        try
        {
            if (execute)
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            else
                frame.Dispose();
        }
        catch (AggregateException exception)
        {
            Assert.IsType<InvalidOperationException>(Assert.Single(exception.InnerExceptions));
            failed = true;
        }
        finally
        {
            frame.Dispose();
        }
        Assert.True(failed);
        Assert.True(result.Called);
        Assert.Equal(execute, result.Submitted);
        Assert.Equal(execute, result.HasCompletions);
        using RenderGraphFrame next = graph.BeginFrame();
    }

    private sealed class CompletionReceiver(GraphPassId pass) : IRenderGraphFrameExecutionObserver
    {
        public bool Called { get; private set; }
        public bool Submitted { get; private set; }
        public bool HasCompletions { get; private set; }
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            Called = true;
            Submitted = result.TryGetPassCompletion(pass, out _);
            HasCompletions = !result.SubmittedCompletions.IsEmpty;
        }
    }

    private sealed class ThrowingCompletionReceiver : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) =>
            throw new InvalidOperationException("Completion callback failed");
    }

    [Fact]
    public void InvalidCompletionDestinationLeavesFrameAvailableForRetry()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            bool rejected = false;
            try { frame.Execute([]); }
            catch (ArgumentException) { rejected = true; }
            Assert.True(rejected);
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            Assert.Equal(0, frame.Execute(completions));
        }
        using RenderGraphFrame next = graph.BeginFrame();
    }
}
