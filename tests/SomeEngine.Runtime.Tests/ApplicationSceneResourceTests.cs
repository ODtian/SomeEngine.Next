using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationSceneResourceTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SceneResourcesPrecedeGeometryAndAllInitializersAndOutliveShutdown(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        SceneResource? resource = null;
        application.Initialize += _ => resource!.Observe("initialize:first");
        application.CreateSceneResources = (world, scene) =>
        {
            Assert.NotEmpty(scene.MeshEntities);
            return resource = new SceneResource(world, events, "scene");
        };
        application.InstallGeometry(new GeometryFeature(_ =>
        {
            resource!.Observe("geometry");
            return null;
        }));
        application.Initialize += _ => resource!.Observe("initialize:last");
        application.Shutdown += _ => resource!.Observe("shutdown:first");
        application.Shutdown += _ => resource!.Observe("shutdown:last");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(1, result.CompletedFrames);
        Assert.NotNull(resource);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
        Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        Assert.Equal(["create:scene", "geometry", "initialize:first", "initialize:last",
            "shutdown:first", "shutdown:last", "dispose:scene", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task LaterInitializerFailureStillReleasesSceneResourcesAfterShutdown(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var failure = new InvalidOperationException();
        SceneResource? resource = null;
        application.CreateSceneResources = (world, _) => resource = new SceneResource(world, events, "scene");
        application.Initialize += _ => resource!.Observe("initialize:first");
        application.Initialize += _ =>
        {
            resource!.Observe("initialize:throw");
            throw failure;
        };
        application.FrameCompleted += _ => events.Add("unexpected frame");
        application.Shutdown += _ => resource!.Observe("shutdown:first");
        application.Shutdown += _ => resource!.Observe("shutdown:last");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error?.GetBaseException());
        Assert.NotNull(resource);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
        Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        Assert.Equal(["create:scene", "initialize:first", "initialize:throw", "shutdown:first",
            "shutdown:last", "dispose:scene", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ThrowingShutdownObserverStillReleasesSceneResourcesBeforeWorld(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var frames = new List<int>();
        var failure = new InvalidOperationException();
        SceneResource? resource = null;
        application.CreateSceneResources = (world, _) => resource = new SceneResource(world, events, "scene");
        application.Initialize += _ => resource!.Observe("initialize");
        application.FrameCompleted += frame => frames.Add(frame.Index);
        application.Shutdown += _ => resource!.Observe("shutdown:first");
        application.Shutdown += _ =>
        {
            resource!.Observe("shutdown:throw");
            throw failure;
        };
        application.Shutdown += _ => events.Add("unexpected shutdown");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error?.GetBaseException());
        Assert.Equal([0], frames);
        Assert.NotNull(resource);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
        Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        Assert.Equal(["create:scene", "initialize", "shutdown:first", "shutdown:throw",
            "dispose:scene", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task GeometryCleanupFailureStillReleasesSceneResourcesBeforeWorld(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var frames = new List<int>();
        var failure = new InvalidOperationException();
        SceneResource? resource = null;
        application.CreateSceneResources = (world, _) => resource = new SceneResource(world, events, "scene");
        application.InstallGeometry(new GeometryFeature(_ => new CleanupAction(() =>
        {
            resource!.Observe("geometry:dispose");
            throw failure;
        })));
        application.Initialize += _ => resource!.Observe("initialize");
        application.FrameCompleted += frame => frames.Add(frame.Index);
        application.Shutdown += _ => resource!.Observe("shutdown");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error?.GetBaseException());
        Assert.Equal([0], frames);
        Assert.NotNull(resource);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
        Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        Assert.Equal(["create:scene", "initialize", "shutdown", "geometry:dispose",
            "dispose:scene", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SceneResourceCleanupFailureDoesNotPreventWorldDisposal(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var failure = new InvalidOperationException();
        SceneResource? resource = null;
        application.CreateSceneResources = (world, _) =>
        {
            resource = new SceneResource(world, events, "scene");
            return new CleanupAction(() =>
            {
                resource.Dispose();
                throw failure;
            });
        };
        application.Initialize += _ => resource!.Observe("initialize");
        application.Shutdown += _ => resource!.Observe("shutdown");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error?.GetBaseException());
        Assert.NotNull(resource);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
        Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        Assert.Equal(["create:scene", "initialize", "shutdown", "dispose:scene", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SceneResourceFactoryFailureDisposesWorldWithoutInvokingInitializers(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var failure = new InvalidOperationException();
        World? createdWorld = null;
        application.CreateSceneResources = (world, _) =>
        {
            createdWorld = world;
            events.Add("create:throw");
            throw failure;
        };
        application.Initialize += _ => events.Add("unexpected initialize");
        application.Shutdown += _ => events.Add("unexpected shutdown");
        application.SceneCompleted += _ => events.Add("completed");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error?.GetBaseException());
        Assert.NotNull(createdWorld);
        Assert.Throws<ObjectDisposedException>(() => createdWorld.Commands());
        Assert.Equal(["create:throw", "completed"], events);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ReloadCreatesDistinctWorldResourcesAndReleasesEachExactlyOnce(string backend)
    {
        var application = CreateApplication(backend);
        var events = new List<string>();
        var resources = new List<SceneResource>();
        var frames = new List<int>();
        application.CreateSceneResources = (world, _) =>
        {
            if (resources.Count != 0)
            {
                Assert.Equal(1, resources[^1].DisposeCalls);
                Assert.Throws<ObjectDisposedException>(() => resources[^1].World.Commands());
            }
            var resource = new SceneResource(world, events, (resources.Count + 1).ToString());
            resources.Add(resource);
            return resource;
        };
        application.Initialize += frame =>
        {
            Assert.Same(resources[^1].World, frame.World);
            resources[^1].Observe($"initialize:{resources.Count}");
        };
        application.FrameCompleted += frame =>
        {
            frames.Add(frame.Index);
            if (resources.Count < 3) application.Reload();
        };
        application.Shutdown += frame =>
        {
            Assert.Same(resources[^1].World, frame.World);
            resources[^1].Observe($"shutdown:{resources.Count}");
        };
        application.SceneCompleted += _ => events.Add($"completed:{resources.Count}");

        ApplicationResult result = await Application.RunAsync([], application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(3, result.CompletedFrames);
        Assert.Equal([0, 0, 0], frames);
        Assert.Equal(3, resources.Count);
        Assert.NotSame(resources[0].World, resources[1].World);
        Assert.NotSame(resources[0].World, resources[2].World);
        Assert.NotSame(resources[1].World, resources[2].World);
        Assert.All(resources, resource =>
        {
            Assert.Equal(1, resource.DisposeCalls);
            Assert.Equal((Vector3?)new Vector3(7, 11, 13), resource.PositionAtDisposal);
            Assert.Throws<ObjectDisposedException>(() => resource.World.Commands());
        });
        Assert.Equal(["create:1", "initialize:1", "shutdown:1", "dispose:1", "completed:1",
            "create:2", "initialize:2", "shutdown:2", "dispose:2", "completed:2",
            "create:3", "initialize:3", "shutdown:3", "dispose:3", "completed:3"], events);
    }

    private ApplicationScene CreateApplication(string backend) => new(content.DirectoryPath)
    {
        RegisterSceneComponents = SampleSceneSerialization.Register,
        Options = RuntimeStartupOptions.Parse(["--graphics-backend", backend, "--frames", "1", "--hidden-window",
            "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm"]),
    };

    private sealed class SceneResource : IDisposable
    {
        private readonly List<string> _events;
        private readonly string _name;
        private readonly Entity _marker;

        internal SceneResource(World world, List<string> events, string name)
        {
            World = world;
            _events = events;
            _name = name;
            _marker = world.CreateEntity(new WorldTransform
                { Qvvs = new TransformQvvs(new Vector3(7, 11, 13), Quaternion.Identity) });
            events.Add($"create:{name}");
        }

        internal World World { get; }
        internal int DisposeCalls { get; private set; }
        internal Vector3? PositionAtDisposal { get; private set; }

        internal void Observe(string name)
        {
            Assert.Equal(0, DisposeCalls);
            Assert.True(World.IsAlive(_marker));
            Assert.Equal(new Vector3(7, 11, 13), World.Read<WorldTransform>(_marker).Qvvs.Position);
            _events.Add(name);
        }

        public void Dispose()
        {
            DisposeCalls++;
            PositionAtDisposal = World.Read<WorldTransform>(_marker).Qvvs.Position;
            World.DestroyEntity(_marker);
            _events.Add($"dispose:{_name}");
        }
    }

    private sealed class GeometryFeature(Func<ApplicationGeometryContext, IDisposable?> install) : IApplicationGeometryFeature
    {
        public IDisposable? Install(ApplicationGeometryContext context) => install(context);
    }

    private sealed class CleanupAction(Action cleanup) : IDisposable
    {
        public void Dispose() => cleanup();
    }
}
