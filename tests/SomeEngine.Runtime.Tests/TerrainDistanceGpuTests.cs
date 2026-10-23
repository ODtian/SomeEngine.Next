using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainDistanceGpuTests(SceneContentFixture content)
{
    [Fact]
    public async Task CpuAndGpuRebuildTheSameCapturedDistanceSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var cpuTerrain = new Terrain(new TerrainEditing(scheduler));
        using var gpuTerrain = new Terrain(new TerrainEditing(scheduler));
        var grid = new VoxelGrid(2, .5f);
        VoxelField cpu = cpuTerrain.CreateVoxelField("CPU plane", grid);
        VoxelField gpu = gpuTerrain.CreateVoxelField("GPU plane", grid);
        var range = new VoxelRegion(-2, -2, -1, 5, 5, 3);
        var precision = new VoxelDistancePrecision(.005f, .01f);
        var settings = new VoxelDistanceSettings(range, 4, precision);
        var input = new PlaneSamples(settings.ReadRegion);
        await cpu.EditCpu(input).WaitAsync();
        await gpu.EditCpu(input).WaitAsync();
        VoxelFieldRead source = await gpu.ReadCpuAsync(range);
        await cpu.RebuildDistanceCpu(settings, new SubdividedVoxelDistance(9)).WaitAsync();
        VoxelFieldRead expected = await cpu.ReadCpuAsync(range);
        Shader shader = await TerrainGpuEditingTests.LoadNeighborhoodShaderAsync("terrain_voxel_distance.slang");
        TerrainEdit edit = gpu.RebuildDistanceGpu(settings, new SubdividedVoxelDistanceGpu(shader, 9));
        Task<VoxelFieldRead> captured = gpu.ReadCpuAsync(range).AsTask();
        TerrainEdit later = gpu.EditCpu(new AddScalar(range, .25f));
        Task<VoxelFieldRead> latest = gpu.ReadCpuAsync(range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
        };
        application.InstallGeometry(new TerrainGpuEditingTests.FieldFeature(gpuTerrain));

        Assert.Equal(0, await Application.RunAsync([], application: application));
        await edit.WaitAsync();
        VoxelFieldRead actual = await captured;
        VoxelFieldRead current = await latest;
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
        {
            Vector3 point = grid.SamplePosition(x, y, z);
            float distance = (3f * point.X + 4f * point.Y - 1f) / 5f;
            Assert.Equal(distance, actual[x, y, z], tolerance: precision.DistanceTolerance);
            Assert.Equal(expected[x, y, z], actual[x, y, z], tolerance: precision.DistanceTolerance);
            Assert.Equal(distance + .25f, current[x, y, z], tolerance: precision.DistanceTolerance);
        }
        Assert.Equal(-2f, source[0, 0, 0]);
        Assert.Equal(edit.Revision, actual.Revision);
        Assert.Equal(later.Revision, current.Revision);
        Assert.Equal(0f, actual.Sample(new(1f / 3f, 0, .125f)), tolerance: precision.DistanceTolerance);
        Assert.Equal(0f, actual.Sample(new(0, .25f, .125f)), tolerance: precision.DistanceTolerance);
        float xCrossing = .5f * actual[0, 0, 0] / (actual[0, 0, 0] - actual[1, 0, 0]);
        float yCrossing = .5f * actual[0, 0, 0] / (actual[0, 0, 0] - actual[0, 1, 0]);
        Assert.Equal(1f / 3f, xCrossing, tolerance: precision.SurfaceTolerance);
        Assert.Equal(.25f, yCrossing, tolerance: precision.SurfaceTolerance);
    }

    private readonly record struct PlaneSamples(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z)
        {
            Vector3 point = source.Grid.SamplePosition(x, y, z);
            return (3f * point.X + 4f * point.Y - 1f) * (2f + .1f * point.Z);
        }
    }

    private readonly record struct AddScalar(VoxelRegion Range, float Value) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] + Value;
    }
}
