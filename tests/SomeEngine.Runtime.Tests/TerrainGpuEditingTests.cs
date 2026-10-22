using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Systems;
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
