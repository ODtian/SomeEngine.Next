using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Graph = SomeEngine.RenderGraph.RenderGraph;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationShutdownTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SceneFailureSurvivesFeatureDisposalAndTheNextApplicationCompletes(string backend)
    {
        var sceneError = new InvalidOperationException();
        var disposalError = new FormatException();
        Graph? graph = null;
        Device? device = null;
        ApplicationOptions options = ApplicationOptions.Parse([
            "--graphics-backend", backend, "--hidden-window", "--synchronous-frames", "--static-scene",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]);
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = options,
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) throw sceneError;
            },
            CreateFrameFeatures = rendering =>
            {
                graph = rendering.Graph;
                device = rendering.Device;
                return [new DisposalFailureFeature(disposalError)];
            },
        };

        ApplicationResult result = await Application.RunAsync([], scene);

        Assert.Equal(ApplicationFailure.SceneAction, result.Failure);
        Assert.Equal(2, result.CompletedFrames);
        var errors = Assert.IsType<AggregateException>(result.Error!.InnerException);
        Assert.Same(sceneError, errors.InnerExceptions[0].GetBaseException());
        Assert.Contains(errors.Flatten().InnerExceptions, error => ReferenceEquals(error, disposalError));
        Assert.NotNull(graph);
        Assert.True(graph.IsDisposed);
        Assert.NotNull(device);
        Assert.Equal(DeviceStatus.Disposed, device.Status);

        var nextScene = new ApplicationScene(content.DirectoryPath)
        {
            Options = options with { FrameLimit = 3 },
        };
        ApplicationResult next = await Application.RunAsync([], nextScene);

        Assert.True(next.Succeeded, next.Error?.ToString());
        Assert.Equal(3, next.CompletedFrames);
    }

    private sealed class DisposalFailureFeature(Exception failure) : IApplicationFrameFeature
    {
        public void Reconcile() { }
        public void Record(ref RenderGraphFrame frame, int simulationFrame) { }
        public void Dispose() => throw failure;
    }
}
