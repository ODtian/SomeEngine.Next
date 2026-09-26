namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameObjectsWaitForSubmittedWorkEvenWhenTheNextFrameIsCancelled(bool cancelNextFrame)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddGeneralFramePass("retirement work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                0, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref int data) => { },
                static (ref GeneralPassCommandScope commands, in int data) => { });
            edit.Commit();
        }
        var objects = Enumerable.Range(0, 128).Select(_ => new DisposalCount()).ToArray();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                if (!cancelNextFrame)
                    foreach (DisposalCount value in objects) frame.RetireAfterFrame(value);
                frame.Execute(completions);
            }
            if (cancelNextFrame)
            {
                using RenderGraphFrame frame = graph.BeginFrame();
                foreach (DisposalCount value in objects) frame.RetireAfterFrame(value);
            }
            graph.CollectCompleted();
            Assert.All(objects, value => Assert.Equal(0, value.Count));
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.All(objects, value => Assert.Equal(1, value.Count));
    }

    [Fact]
    public void CompletionCallbackFailureStillReleasesAllFrameObjects()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var objects = Enumerable.Range(0, 8).Select(_ => new DisposalCount()).ToArray();
        RenderGraphFrame frame = graph.BeginFrame();
        frame.ObserveExecution(new FailingCallback());
        foreach (DisposalCount value in objects) frame.RetireAfterFrame(value);
        bool failed = false;
        try { frame.Dispose(); }
        catch (AggregateException) { failed = true; }
        Assert.True(failed);
        graph.CollectCompleted();
        Assert.All(objects, value => Assert.Equal(1, value.Count));
        using RenderGraphFrame nextFrame = graph.BeginFrame();
    }

    private sealed class DisposalCount : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }

    private sealed class FailingCallback : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
            => throw new InvalidOperationException("Test callback failed.");
    }
}
