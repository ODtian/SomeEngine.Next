using DefaultGame;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainFeatureLifetimeTests(SceneContentFixture content)
{
    [Fact]
    public async Task RemovedGpuPlacementsReturnTheirCapacityBeforeTheNextPlacementsBuild()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuHeightTerrainExample ground = await GpuHeightTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        using var material = new UnlitMaterial();
        var builds = new List<TerrainGpuBuild>();
        (int First, int Second) placement = default;
        SceneImage? empty = null, visible = null;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            GeometryMemoryBytes = 256 * 1024,
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "15"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                if (frame.Index <= 12 && frame.Index % 2 == 0)
                    placement = ground.AddPlacements(material, material);
                if (frame.Index <= 11 && frame.Index % 2 == 1)
                {
                    ground.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                    builds.Add(ground.Scene.GetGpuBuild(ground.Heights.Terrain));
                    ground.Scene.Remove(placement.First);
                    ground.Scene.Remove(placement.Second);
                }
                if (frame.Index == 11) frame.Scene.CaptureImage(image => empty = image);
                if (frame.Index == 13) ground.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 14)
                {
                    builds.Add(ground.Scene.GetGpuBuild(ground.Heights.Terrain));
                    frame.Scene.CaptureImage(image => visible = image);
                }
            },
        };
        ground.Install(application);

        Assert.True(await Application.RunAsync([], application: application) == 0, completed?.Error?.ToString());
        Assert.Equal(7, builds.Count);
        Assert.All(builds, build =>
        {
            Assert.True(build.Completion.IsCompleted);
            Assert.True(build.Visibility.IsCompleted);
        });
        SceneImages.AssertChanged(empty!, visible!);
    }

    [Fact]
    public async Task ReloadFinishesRenderedAndNextGameUpdateGeometryBatches()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuHeightTerrainExample ground = await GpuHeightTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        using var material = new UnlitMaterial();
        ground.AddPlacements(material, material);
        var builds = new List<TerrainGpuBuild>();
        int runs = 0, updates = 0;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneStarted = _ => runs++,
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "2"]),
        };
        application.FrameCompleted = _ =>
        {
            if (runs != 1) return;
            application.Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"]);
            application.Reload();
        };
        application.InstallGeometry(new GameActionFeature(() =>
        {
            if (runs != 1 || ++updates != 2) return;
            ground.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
            ground.Heights.EditCpu(new HeightDeltaBrush(ground.Heights.Grid.Range, .5f)).Completion.Complete();
        }));
        ground.Install(application);
        application.InstallGeometry(new GameActionFeature(() => builds.Add(ground.Scene.GetGpuBuild(ground.Heights.Terrain))));

        Assert.True(await Application.RunAsync([], application: application) == 0, completed?.Error?.ToString());
        Assert.Equal(2, runs);
        Assert.Equal(3, builds.Count);
        Assert.True(builds[1].Revision > builds[0].Revision);
        Assert.All(builds, build =>
        {
            Assert.True(build.Completion.IsCompleted);
            Assert.True(build.Visibility.IsCompleted);
        });
    }

    [Fact]
    public async Task StoppingAfterACanceledFrameCancelsFieldReadsAndFinishesCpuGeometryWork()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var ground = new HeightTerrainExample(scheduler);
        Shader shader = await TerrainGpuEditingTests.LoadNeighborhoodShaderAsync("terrain_height_delta.slang");
        TerrainEdit edit = default;
        Task<HeightFieldRead>? read = null;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    Material material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.First());
                    ground.AddPlacements(material, material);
                }
                if (frame.Index == 1) ground.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2)
                {
                    edit = ground.AddHeightGpu(shader, 1f);
                    read = ground.Heights.ReadCpuAsync(ground.Heights.Grid.Range).AsTask();
                    frame.Scene.CancelFrame();
                }
            },
        };
        ground.Install(application);

        Assert.True(await Application.RunAsync([], application: application) == 0, completed?.Error?.ToString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit.WaitAsync().AsTask());
    }

    private sealed class GameActionFeature(Action update) : IApplicationGeometryFeature
    {
        public IDisposable? Install(ApplicationGeometryContext context)
        {
            context.AddGameSystem(new GameAction(update));
            return null;
        }
    }

    private sealed class GameAction(Action update) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => update();
    }
}
