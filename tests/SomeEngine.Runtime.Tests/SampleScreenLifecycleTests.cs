using System.Diagnostics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class SampleScreenLifecycleTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task OneFrameScenePlatformPreservesSampleUiLifetime(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--frames", "1", "--hidden-window",
            "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync",
            "--surface-panel", "--hud"];
        // The application overload uses Application.Options rather than reparsing args.
        var platform = new SceneTestPlatform(
            [new TestScene("Sample UI lifecycle", content.DirectoryPath)], RuntimeStartupOptions.Parse(args), SampleSceneOptions.Parse(args));
        ApplicationScene application = platform.Application;
        application.MeasureInterface = true;
        var events = new List<string>();
        var images = new List<SceneImage>();
        var completions = new List<ApplicationResult>();
        var probe = new MeasuredGameSystem();
        Entity atlas = Entity.Null;
        Entity[] sampleLabels = [];
        int width = 0, height = 0;
        var shutdownStates = new List<(bool AtlasAlive, int AtlasCount, bool LabelsAlive)>();
        bool? atlasAliveAfterTeardown = null;
        int? atlasCountAfterTeardown = null;
        bool sampleLabelsDeadAfterTeardown = false;

        // Append after the sample's installed callbacks: do not replace its ownership hooks.
        application.Initialize += frame =>
        {
            events.Add("initialize");
            atlas = Assert.Single(AtlasEntities(frame.World));
            Assert.True(frame.World.IsAlive(atlas));
            FrameStageDescription[] stages = frame.Systems.Describe();
            int game = Array.FindIndex(stages, stage => stage.Stage == FrameStage.GameInputAndSimulation);
            int rebuild = Array.FindIndex(stages, stage => stage.Stage == FrameStage.ViewRebuild);
            Assert.True(game >= 0 && game < rebuild);
            Type sampleSystem = Assert.Single(stages[game].SystemTypes,
                type => type.FullName == "DefaultGame.ScenePanels");
            Assert.Equal(typeof(SceneTestPlatform).Assembly, sampleSystem.Assembly);
            // Extraction owns feature disposal even when initialization fails before any update.
            // Register after ScenePanels so this observer sees owner cleanup while World is live.
            frame.Extraction.Add(new CleanupObservation(() =>
            {
                events.Add("cleanup observed");
                atlasAliveAfterTeardown = frame.World.IsAlive(atlas);
                atlasCountAfterTeardown = AtlasEntities(frame.World).Length;
                sampleLabelsDeadAfterTeardown = sampleLabels.Length > 0 &&
                    sampleLabels.All(entity => !frame.World.IsAlive(entity));
            }));
            frame.Systems.AddGameSystem(probe);
        };
        application.UpdateFrame += frame =>
        {
            events.Add("update");
            Assert.Equal(0, frame.Index);
            (width, height) = (frame.Width, frame.Height);
            probe.Armed = true;
            frame.Scene.CaptureImage(image =>
            {
                events.Add("capture");
                images.Add(image);
            });
        };
        application.FrameCompleted += frame =>
        {
            events.Add("completed");
            Assert.Equal(0, frame.Index);
            Assert.Equal(0, frame.Timing.SimulationFrame);
            Assert.Equal(atlas, Assert.Single(AtlasEntities(frame.World)));
            string[] expected = ["Hello SomeEngine", "中文你好世界", "Add one", "Clicks: 0",
                "SomeUI HUD", "799", "N00:000", "N99:000", "10000 items"];
            sampleLabels = expected.Select(text => FindText(frame.World, text)).ToArray();
            Assert.All(sampleLabels, entity => Assert.True(frame.World.IsAlive(entity)));
            Assert.Equal(1, probe.MeasuredUpdates);
            Assert.True(double.IsFinite(frame.Timing.Views.TotalMs) && frame.Timing.Views.TotalMs > 0);
            // The game-system stage is inside the same total time/allocation measurement as views.
            Assert.True(frame.Timing.Views.TotalMs >= probe.ElapsedMs);
            Assert.True(probe.AllocatedBytes >= MeasuredGameSystem.AllocationSize);
            Assert.True(frame.Timing.Views.AllocatedBytes >= probe.AllocatedBytes);
        };
        // Preserve the original runtime contract: every Shutdown observer sees live sample UI.
        application.Shutdown += frame => ObserveShutdown("shutdown:first", frame);
        application.Shutdown += frame => ObserveShutdown("shutdown:last", frame);
        application.SceneCompleted += result =>
        {
            events.Add("scene completed");
            completions.Add(result);
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, $"{result.Failure}; frames={result.CompletedFrames}; {result.Error}");
        Assert.Equal(1, result.CompletedFrames);
        Assert.NotEqual(Entity.Null, atlas);
        Assert.Equal(2, shutdownStates.Count);
        Assert.All(shutdownStates, state =>
        {
            Assert.True(state.AtlasAlive);
            Assert.Equal(1, state.AtlasCount);
            Assert.True(state.LabelsAlive);
        });
        Assert.Equal(false, atlasAliveAfterTeardown);
        Assert.Equal(0, atlasCountAfterTeardown);
        Assert.True(sampleLabelsDeadAfterTeardown);
        Assert.Equal(["initialize", "update", "capture", "completed", "shutdown:first", "shutdown:last",
            "cleanup observed", "scene completed"], events);
        ApplicationResult completed = Assert.Single(completions);
        Assert.True(completed.Succeeded, completed.Error?.ToString());
        Assert.Equal(1, completed.CompletedFrames);
        SceneImage captured = Assert.Single(images);
        Assert.Equal(0, captured.Frame);
        Assert.True(width > 0 && height > 0);
        Assert.Equal(width, captured.Width);
        Assert.Equal(height, captured.Height);
        Assert.Equal(checked(width * height * 4), captured.Pixels.Length);
        Assert.False(application.ReloadRequested);
        Assert.False(platform.IsMeasuring);
        Assert.True(platform.HudOverlayEnabled);

        void ObserveShutdown(string name, ApplicationFrame frame)
        {
            events.Add(name);
            shutdownStates.Add((frame.World.IsAlive(atlas), AtlasEntities(frame.World).Length,
                sampleLabels.Length > 0 && sampleLabels.All(entity => frame.World.IsAlive(entity))));
        }
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LaterInitializeFailureKeepsUiLiveThroughShutdownAndStillCleansIt(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--frames", "1", "--hidden-window",
            "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync",
            "--surface-panel", "--hud"];
        var platform = new SceneTestPlatform(
            [new TestScene("Sample UI initialization failure", content.DirectoryPath)],
            RuntimeStartupOptions.Parse(args), SampleSceneOptions.Parse(args));
        ApplicationScene application = platform.Application;
        var expectedFailure = new InvalidOperationException("Expected failure in an appended Initialize observer.");
        var events = new List<string>();
        var completions = new List<ApplicationResult>();
        var shutdownStates = new List<(bool AtlasAlive, bool CanvasesAlive)>();
        Entity atlas = Entity.Null;
        Entity[] canvases = [];
        bool? atlasAliveAfterCleanup = null;
        int? atlasCountAfterCleanup = null;
        int? canvasCountAfterCleanup = null;
        bool canvasesDeadAfterCleanup = false;

        // This later observer is deliberately outside the sample install-time captured callback.
        // Throw before the runtime's first UpdateViews: no game-system OnCreate/OnDestroy is available.
        application.Initialize += frame =>
        {
            events.Add("initialize");
            atlas = Assert.Single(AtlasEntities(frame.World));
            canvases = ScreenCanvases(frame.World);
            Assert.Equal(2, canvases.Length);
            Assert.True(frame.World.IsAlive(atlas));
            Assert.All(canvases, entity => Assert.True(frame.World.IsAlive(entity)));
            frame.Extraction.Add(new CleanupObservation(() =>
            {
                events.Add("cleanup observed");
                atlasAliveAfterCleanup = frame.World.IsAlive(atlas);
                atlasCountAfterCleanup = AtlasEntities(frame.World).Length;
                canvasCountAfterCleanup = ScreenCanvases(frame.World).Length;
                canvasesDeadAfterCleanup = canvases.Length == 2 &&
                    canvases.All(entity => !frame.World.IsAlive(entity));
            }));
            throw expectedFailure;
        };
        application.UpdateFrame += _ => events.Add("unexpected update");
        application.FrameCompleted += _ => events.Add("unexpected completed frame");
        application.Shutdown += frame => ObserveShutdown("shutdown:first", frame);
        application.Shutdown += frame => ObserveShutdown("shutdown:last", frame);
        application.SceneCompleted += result =>
        {
            events.Add("scene completed");
            completions.Add(result);
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.CompletedFrames);
        Assert.Same(expectedFailure, result.Error?.GetBaseException());
        Assert.Equal(2, shutdownStates.Count);
        Assert.All(shutdownStates, state =>
        {
            Assert.True(state.AtlasAlive);
            Assert.True(state.CanvasesAlive);
        });
        Assert.Equal(false, atlasAliveAfterCleanup);
        Assert.Equal(0, atlasCountAfterCleanup);
        Assert.Equal(0, canvasCountAfterCleanup);
        Assert.True(canvasesDeadAfterCleanup);
        Assert.Equal(["initialize", "shutdown:first", "shutdown:last", "cleanup observed", "scene completed"], events);
        ApplicationResult completed = Assert.Single(completions);
        Assert.False(completed.Succeeded);
        Assert.Equal(0, completed.CompletedFrames);
        Assert.Same(expectedFailure, completed.Error?.GetBaseException());
        Assert.False(application.ReloadRequested);

        void ObserveShutdown(string name, ApplicationFrame frame)
        {
            events.Add(name);
            shutdownStates.Add((frame.World.IsAlive(atlas),
                canvases.Length == 2 && canvases.All(entity => frame.World.IsAlive(entity))));
        }
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ThrowingShutdownObserverStillCleansSampleUiAndRetainsItsFailure(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--frames", "1", "--hidden-window",
            "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync",
            "--surface-panel", "--hud"];
        var platform = new SceneTestPlatform(
            [new TestScene("Sample UI shutdown failure", content.DirectoryPath)], RuntimeStartupOptions.Parse(args), SampleSceneOptions.Parse(args));
        ApplicationScene application = platform.Application;
        var expectedFailure = new InvalidOperationException("Expected failure in an appended Shutdown observer.");
        var events = new List<string>();
        var completedFrames = new List<int>();
        var completions = new List<ApplicationResult>();
        var shutdownStates = new List<(bool AtlasAlive, bool CanvasesAlive)>();
        Entity atlas = Entity.Null;
        Entity[] canvases = [];
        bool throwingCallbackReached = false;
        bool? atlasAliveAfterCleanup = null;
        int? atlasCountAfterCleanup = null;
        int? canvasCountAfterCleanup = null;
        bool canvasesDeadAfterCleanup = false;

        application.Initialize += frame =>
        {
            events.Add("initialize");
            atlas = Assert.Single(AtlasEntities(frame.World));
            canvases = ScreenCanvases(frame.World);
            Assert.Equal(2, canvases.Length);
            frame.Extraction.Add(new CleanupObservation(() =>
            {
                events.Add("cleanup observed");
                atlasAliveAfterCleanup = frame.World.IsAlive(atlas);
                atlasCountAfterCleanup = AtlasEntities(frame.World).Length;
                canvasCountAfterCleanup = ScreenCanvases(frame.World).Length;
                canvasesDeadAfterCleanup = canvases.Length == 2 &&
                    canvases.All(entity => !frame.World.IsAlive(entity));
            }));
        };
        application.UpdateFrame += frame =>
        {
            events.Add("update");
            Assert.Equal(0, frame.Index);
        };
        application.FrameCompleted += frame =>
        {
            events.Add("completed");
            completedFrames.Add(frame.Index);
        };
        application.Shutdown += frame => ObserveShutdown("shutdown:before", frame);
        application.Shutdown += frame =>
        {
            ObserveShutdown("shutdown:throw", frame);
            throwingCallbackReached = true;
            throw expectedFailure;
        };
        // Normal multicast semantics stop here: this later observer must not be invoked.
        application.Shutdown += _ => events.Add("unexpected shutdown:after");
        application.SceneCompleted += result =>
        {
            events.Add("scene completed");
            completions.Add(result);
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(throwingCallbackReached, $"The owned Shutdown failure was not reached: {result.Error}");
        Assert.False(result.Succeeded);
        Assert.Same(expectedFailure, result.Error?.GetBaseException());
        // Shutdown fails in a finally block, outside the submitted-frame exception wrapper.
        // Verify the observed frame rather than freezing unrelated failure-result accounting.
        Assert.Equal([0], completedFrames);
        Assert.Equal(2, shutdownStates.Count);
        Assert.All(shutdownStates, state =>
        {
            Assert.True(state.AtlasAlive);
            Assert.True(state.CanvasesAlive);
        });
        Assert.Equal(false, atlasAliveAfterCleanup);
        Assert.Equal(0, atlasCountAfterCleanup);
        Assert.Equal(0, canvasCountAfterCleanup);
        Assert.True(canvasesDeadAfterCleanup);
        Assert.Equal(["initialize", "update", "completed", "shutdown:before", "shutdown:throw",
            "cleanup observed", "scene completed"], events);
        ApplicationResult completed = Assert.Single(completions);
        Assert.False(completed.Succeeded);
        Assert.Same(expectedFailure, completed.Error?.GetBaseException());
        Assert.False(application.ReloadRequested);

        void ObserveShutdown(string name, ApplicationFrame frame)
        {
            events.Add(name);
            shutdownStates.Add((frame.World.IsAlive(atlas),
                canvases.Length == 2 && canvases.All(entity => frame.World.IsAlive(entity))));
        }
    }

    private static Entity[] ScreenCanvases(World world)
    {
        var query = world.Query(world.QueryDefinition().Read<Canvas>());
        var entities = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, ref entities, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                    if (row.Read<Canvas>().Target.Kind == CanvasTargetKind.Screen) value.Add(row.Entity);
            });
        }
        finally { world.ReleaseQuery(query); }
        return entities.ToArray();
    }

    // Only a disposal observer: it declares/collects/writes no render data and owns no resources.
    private sealed class CleanupObservation(Action observe) : IRenderExtractionSystem
    {
        public void DeclareReads(RenderExtractionQuery query) { }
        public void Reset() { }
        public void Collect(QueryChunkView chunk) { }
        public void Apply(RenderExtractionContext context) { }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => false;
        public void ApplyChanges(RenderExtractionContext context) { }
        public void Dispose() => observe();
    }

    private static Entity[] AtlasEntities(World world)
    {
        var query = world.Query(world.QueryDefinition().Read<TextAtlas>());
        var entities = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, ref entities, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows) value.Add(row.Entity);
            });
        }
        finally { world.ReleaseQuery(query); }
        return entities.ToArray();
    }

    private static Entity FindText(World world, string text)
    {
        var query = world.Query(world.QueryDefinition().Read<TextContent>());
        var result = (Text: text, Node: Entity.Null);
        try
        {
            world.ExecuteQuery(query, ref result, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                    if (row.Read<TextContent>().Value == value.Text) value.Node = row.Entity;
            });
        }
        finally { world.ReleaseQuery(query); }
        Assert.NotEqual(Entity.Null, result.Node);
        return result.Node;
    }

    // A test-local implementation of the normal public extension point, not an engine hook.
    private sealed class MeasuredGameSystem : ISystem<ImmediateSystemContext>
    {
        public const int AllocationSize = 4096;
        public bool Armed { get; set; }
        public int MeasuredUpdates { get; private set; }
        public double ElapsedMs { get; private set; }
        public long AllocatedBytes { get; private set; }
        private byte[]? _retained;

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (!Armed) return; // Ignore the existing zero-delta bootstrap view update.
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _retained = new byte[AllocationSize];
            _retained[0] = 1;
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            MeasuredUpdates++;
            GC.KeepAlive(_retained);
        }
    }
}
