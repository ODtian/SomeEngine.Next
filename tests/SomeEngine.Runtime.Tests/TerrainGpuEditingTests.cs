using System.Numerics;
using DefaultGame;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Systems;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Render.Components;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuEditingTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuHeightDeltaMatchesCpuSamplesAfterTheEditCompletes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new HeightGrid(8, 6, 4);
        var range = new HeightRegion(2, 1, 4, 3);
        HeightField cpu = cpuTerrain.CreateHeightField("ground", grid, 2f);
        HeightField gpu = gpuTerrain.CreateHeightField("ground", grid, 2f);
        Shader shader = await LoadHeightShaderAsync();
        await cpu.EditCpu(new HeightDeltaBrush(range, 3f)).WaitAsync();
        TerrainEdit edit = gpu.EditGpu(new HeightDeltaBrushGpu(shader, range, 3f));
        Task<HeightFieldRead> read = gpu.ReadCpuAsync(grid.Range).AsTask();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
        };
        scene.InstallGeometry(new FieldFeature(gpuTerrain));

        int result = await Application.RunAsync([], application: scene);
        await edit.WaitAsync();
        HeightFieldRead actual = await read;
        HeightFieldRead expected = await cpu.ReadCpuAsync(grid.Range);

        Assert.Equal(0, result);
        Assert.Equal(edit.Revision, actual.Revision);
        Assert.True(edit.Completion.IsCompleted);
        for (int z = 0; z < grid.Depth; z++)
        for (int x = 0; x < grid.Width; x++)
            Assert.Equal(expected[x, z], actual[x, z], precision: 5);
    }

    [Fact]
    public async Task CpuAndGpuEditsReadThePreviouslySubmittedHeightVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 2f);
        Shader shader = await LoadHeightShaderAsync();
        var start = new EditStart();
        height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 3f), scheduler.CreateExternalFenceHandle(start));
        TerrainEdit gpuEdit = height.EditGpu(new HeightDeltaBrushGpu(shader, new HeightRegion(2, 1, 4, 3), 7f));
        Task<HeightFieldRead> captured = height.ReadCpuAsync(height.Grid.Range).AsTask();
        TerrainEdit cpuEdit = height.EditCpu(new ScaleHeightBrush(height.Grid.Range, 4f));
        Task<HeightFieldRead> current = height.ReadCpuAsync(height.Grid.Range).AsTask();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
            UpdateFrame = frame => { if (frame.Index == 0) start.Complete(); },
        };
        scene.InstallGeometry(new FieldFeature(terrain));

        int result = await Application.RunAsync([], application: scene);
        HeightFieldRead gpu = await captured;
        HeightFieldRead cpu = await current;

        Assert.Equal(0, result);
        Assert.Equal(12f, gpu[3, 2]);
        Assert.Equal(5f, gpu[0, 0]);
        Assert.Equal(48f, cpu[3, 2]);
        Assert.Equal(20f, cpu[0, 0]);
        Assert.Equal(gpuEdit.Revision, gpu.Revision);
        Assert.Equal(cpuEdit.Revision, cpu.Revision);
    }

    [Fact]
    public async Task GpuHeightEditMovesBothPlacementsToTheRaisedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new HeightTerrainExample(scheduler);
        Shader shader = await LoadHeightShaderAsync();
        TerrainEdit edit = default;
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    example.AddPlacements(red, green);
                }
                if (frame.Index == 1) example.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) edit = example.AddHeightGpu(shader, 1f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);

        int result = await Application.RunAsync([], application: application);
        await edit.WaitAsync();
        HeightFieldRead heights = await example.Heights.ReadCpuAsync(example.Heights.Grid.Range);
        Assert.Equal(0, result);
        Assert.Equal(1f, heights[2, 2]);

        Mesh ground = CreateGround(0);
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 18,
        [
            new(0, scene =>
            {
                var red = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var green = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(ground, [red], new TransformQvvs(new Vector3(-5, -2, 6), Quaternion.Identity));
                scene.AddEntity(ground, [green], new TransformQvvs(new Vector3(1, -2, 6), Quaternion.Identity));
            }),
            new(8, scene => scene.CaptureImage(image => expectedBefore = image)),
            new(9, _ => ground.Update(CreateGround(1))),
            new(17, scene => scene.CaptureImage(image => expectedAfter = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);
        SceneImages.AssertChanged(before!, after!);

        static Mesh CreateGround(float height)
        {
            Vector3[] positions = [new(0, height, 0), new(4, height, 0), new(0, height, 4), new(4, height, 4)];
            StandardClusterAttributes[] attributes = positions.Select(position =>
                new StandardClusterAttributes(Vector3.UnitY, new(1, 0, 0, 1), new(position.X, position.Z))).ToArray();
            return ClusterMeshBuilder.Build(positions, attributes, [0u, 2, 1, 1, 2, 3], [new(0, 6, 0)],
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Raised ground reference");
        }
    }

    private static async ValueTask<Shader> LoadHeightShaderAsync()
    {
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "SomeEngine.slnx")))
            source = source.Parent;
        using var block = new ShaderSourceBlock { SourceNames = ["terrain_height_delta.slang"] };
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        return await block.CreateAsync(slang, new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_6") }],
            SearchPaths = [Path.Combine(source!.FullName, "assets/Shaders")],
        }, "terrain height delta");
    }

    private sealed class FieldFeature(Terrain terrain) : IApplicationGeometryFeature
    {
        public IDisposable Install(ApplicationGeometryContext context)
        {
            var data = new RenderTerrainData(context.Backend, context.Device, context.Graph);
            context.AddGameSystem(new FieldSourceSystem(terrain));
            context.AddExtractor(new TerrainRenderExtractor());
            context.AddPrepareSystem(data);
            context.AddFrameSystem(data);
            context.AddCompletionObserver(data);
            return data;
        }
    }

    private sealed class FieldSourceSystem(Terrain terrain) : ISystem<ImmediateSystemContext>
    {
        public void OnCreate(ref ImmediateSystemContext context) => context.World.CreateEntity(new TerrainSource(terrain));
        public void OnUpdate(ref ImmediateSystemContext context) { }
    }

    private readonly record struct ScaleHeightBrush(HeightRegion Range, float Scale) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => source[x, z] * Scale;
    }

    private sealed class EditStart : IJobExternalFence
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => _signal.Task.IsCompleted;
        public void OnSignaled(Action<object?> callback, object? state)
        {
            if (IsSignaled) callback(state);
            else _signal.Task.GetAwaiter().OnCompleted(() => callback(state));
        }
        public void Complete() => _signal.SetResult();
    }
}
