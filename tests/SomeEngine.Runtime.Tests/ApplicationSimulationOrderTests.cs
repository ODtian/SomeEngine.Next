using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed partial class ApplicationSimulationOrderTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SynchronousSimulationRunsAfterEveryFrameCallbackAndBeforeGeometryAndViews(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm",
            "--fixed-step", ".25", "--frames", "2"];
        var calls = new List<(string Stage, float Seconds)>();
        var revision = new State<int>(-1);
        float seconds = -1;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(args),
            InterfaceEnabled = true,
            RegisterSceneComponents = SampleSceneSerialization.Register,
            Initialize = frame =>
            {
                calls.Add(("initialize", -1));
                var canvas = frame.World.CreateEntity(new Canvas { Size = new(64, 64) });
                Views.Views.Spawn(frame.World, canvas, new RecordedView(revision, () => calls.Add(("view", seconds))));
                frame.Systems.AddGameSystem(new RecordedSystem(() => calls.Add(("game", seconds))));
                frame.Systems.AddInterfaceSystem(new RecordedSystem(() => calls.Add(("interface", seconds))));
                frame.Systems.AddFlipSystem(new RecordedSystem(() => calls.Add(("flip", seconds))));
                frame.Extraction.Add(new RecordedExtraction(() => calls.Add(("extraction", seconds))));
            },
            BeforeCameraInput = (scene, time) =>
            {
                _ = scene.CameraView;
                seconds = time;
                revision.Value++;
                calls.Add(("before", time));
            },
            SimulateScene = (scene, time) =>
            {
                Assert.NotEmpty(scene.MeshEntities);
                calls.Add(("simulate", time));
            },
            UpdateFrame = frame => calls.Add(("update:first", frame.Index * .25f)),
            FrameCompleted = frame => calls.Add(("completed", frame.Index * .25f)),
            Shutdown = _ => calls.Add(("shutdown", seconds)),
        };
        application.UpdateFrame += frame => calls.Add(("update:last", frame.Index * .25f));
        application.InstallGeometry(new RecordedGeometry(() => calls.Add(("geometry", seconds))));

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(2, result.CompletedFrames);
        Assert.Equal(new (string Stage, float Seconds)[]
        {
            ("initialize", -1), ("game", -1), ("view", -1), ("interface", -1), ("flip", -1),
            ("update:first", 0), ("update:last", 0), ("before", 0), ("simulate", 0),
            ("geometry", 0), ("game", 0), ("view", 0), ("interface", 0), ("flip", 0), ("extraction", 0), ("completed", 0),
            ("update:first", .25f), ("update:last", .25f), ("before", .25f), ("simulate", .25f),
            ("geometry", .25f), ("game", .25f), ("view", .25f), ("interface", .25f), ("flip", .25f),
            ("extraction", .25f), ("completed", .25f), ("shutdown", .25f),
        }, calls);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task AsynchronousSimulationKeepsItsZeroTimeBootstrapAndNextAbsoluteTime(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--hidden-window",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--fixed-step", ".25", "--frames", "2"];
        var calls = new List<(string Stage, float Seconds)>();
        var revision = new State<int>(-1);
        float seconds = -1;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(args),
            InterfaceEnabled = true,
            RegisterSceneComponents = SampleSceneSerialization.Register,
            Initialize = frame =>
            {
                calls.Add(("initialize", -1));
                var canvas = frame.World.CreateEntity(new Canvas { Size = new(64, 64) });
                Views.Views.Spawn(frame.World, canvas, new RecordedView(revision, () => calls.Add(("view", seconds))));
                frame.Systems.AddGameSystem(new RecordedSystem(() => calls.Add(("game", seconds))));
                frame.Systems.AddInterfaceSystem(new RecordedSystem(() => calls.Add(("interface", seconds))));
                frame.Systems.AddFlipSystem(new RecordedSystem(() => calls.Add(("flip", seconds))));
                frame.Extraction.Add(new RecordedExtraction(() => calls.Add(("extraction", seconds))));
            },
            BeforeCameraInput = (scene, time) =>
            {
                _ = scene.CameraView;
                seconds = time;
                revision.Value++;
                calls.Add(("before", time));
            },
            SimulateScene = (scene, time) =>
            {
                Assert.NotEmpty(scene.MeshEntities);
                calls.Add(("simulate", time));
            },
            Shutdown = _ => calls.Add(("shutdown", seconds)),
        };
        application.InstallGeometry(new RecordedGeometry(() => calls.Add(("geometry", seconds))));
        // Either callback would switch the real application to synchronous game updates.
        Assert.Null(application.UpdateFrame);
        Assert.Null(application.FrameCompleted);
        Assert.False(application.Options.SynchronousFrames);

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(2, result.CompletedFrames);
        Assert.Equal(new (string Stage, float Seconds)[]
        {
            ("initialize", -1), ("before", 0), ("geometry", 0), ("game", 0),
            ("view", 0), ("interface", 0), ("flip", 0), ("extraction", 0),
            ("before", .25f), ("simulate", .25f), ("geometry", .25f), ("game", .25f),
            ("view", .25f), ("interface", .25f), ("flip", .25f), ("extraction", .25f), ("shutdown", .25f),
        }, calls);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SimulationCallbacksDoNotClearTheCurrentFrameCancellation(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm",
            "--fixed-step", ".25", "--frames", "2"];
        var beforeTimes = new List<float>();
        var simulationTimes = new List<float>();
        var completed = new List<int>();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(args),
            RegisterSceneComponents = SampleSceneSerialization.Register,
            UpdateFrame = frame =>
            {
                if (frame.Index == 0) frame.Scene.CancelFrame();
            },
            BeforeCameraInput = (scene, time) =>
            {
                _ = scene.CameraView;
                beforeTimes.Add(time);
            },
            SimulateScene = (scene, time) =>
            {
                Assert.NotEmpty(scene.MeshEntities);
                simulationTimes.Add(time);
            },
            FrameCompleted = frame => completed.Add(frame.Index),
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(1, result.CompletedFrames);
        Assert.Equal(new[] { 0f, .25f }, beforeTimes);
        Assert.Equal(new[] { 0f, .25f }, simulationTimes);
        Assert.Equal(new[] { 1 }, completed);
    }

    private readonly record struct RecordedView(State<int> Revision, Action Record) : IView, IComponent
    {
        public BuildTask Build()
        {
            _ = Revision.Value;
            Record();
            Row().Size(10, 10);
            return default;
        }
    }

    private sealed class RecordedGeometry(Action record) : IApplicationGeometryFeature
    {
        public IDisposable? Install(ApplicationGeometryContext context)
        {
            context.AddGameSystem(new RecordedSystem(record));
            return null;
        }
    }

    private sealed class RecordedSystem(Action record) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => record();
    }

    // A non-owning observer of the existing public extraction boundary.
    private sealed class RecordedExtraction(Action record) : IRenderExtractionSystem
    {
        public void DeclareReads(RenderExtractionQuery query) { }
        public void Reset() { }
        public void Collect(QueryChunkView chunk) { }
        public void Apply(RenderExtractionContext context) { }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => false;
        public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
        {
            record();
            return false;
        }
        public void ApplyChanges(RenderExtractionContext context) { }
    }
}
