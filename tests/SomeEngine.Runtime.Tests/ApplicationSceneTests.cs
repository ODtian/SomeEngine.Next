using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationSceneTests(SceneContentFixture content)
{
    [Fact]
    public async Task GameWorkFailureIsObservedBeforePresentingItsFrame()
    {
        int presented = 0;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"]),
            Initialize = frame => frame.Systems.AddGameSystem(new FaultingGameSystem()),
            FrameCompleted = _ => presented++,
        };

        ApplicationResult result = await Application.RunAsync([], scene);

        Assert.False(result.Succeeded);
        Assert.Equal(0, presented);
    }

    [Fact]
    public async Task GameCalculationsFinishBeforeEachPresentedFrameAndApplicationClose()
    {
        var state = new GameCalculations();
        int applicationThread = 0;
        var presented = new List<int>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            Initialize = frame =>
            {
                applicationThread = Environment.CurrentManagedThreadId;
                frame.Systems.AddGameSystem(new CountingGameSystem(state));
            },
            FrameCompleted = _ => presented.Add(Volatile.Read(ref state.Completed)),
        };

        ApplicationResult result = await Application.RunAsync([], scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([1, 2, 3], presented);
        Assert.Equal(3, state.Completed);
        Assert.All(state.CallbackThreads, thread => Assert.Equal(applicationThread, thread));
    }

    private sealed class GameCalculations
    {
        internal readonly List<int> CallbackThreads = [];
        internal int Completed;
    }

    private readonly record struct CountingGameSystem(GameCalculations Values) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Values.CallbackThreads.Add(Environment.CurrentManagedThreadId);
            JobSystem.Schedule(new CountingCalculation(Values));
        }
    }

    private readonly record struct CountingCalculation(GameCalculations Values) : IJob
    {
        public void Execute() => JobSystem.Schedule(new CalculationOutput(Values));
    }

    private readonly record struct CalculationOutput(GameCalculations Values) : IJob
    {
        public void Execute() => Interlocked.Increment(ref Values.Completed);
    }

    private readonly struct FaultingGameSystem : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => JobSystem.Schedule(new FaultingCalculation());
    }

    private readonly struct FaultingCalculation : IJob
    {
        public void Execute() => throw new InvalidOperationException("frame game calculation failed");
    }

    [Fact]
    public async Task FrameActionsCaptureSelectedFrames()
    {
        SceneImage? captured = null;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captured = image);
            },
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Frame);
        Assert.Equal(64 * 64 * 4, captured.Pixels.Length);
    }

    [Fact]
    public async Task ReloadRestartsFramesWithUpdatedSettings()
    {
        var frames = new List<int>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame => frames.Add(frame.Index),
        };
        scene.FrameCompleted = frame =>
        {
            if (frame.Index != 1) return;
            scene.Options = scene.Options with { FrameLimit = 1 };
            scene.Reload();
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.Equal([0, 1, 0], frames);
    }
}
