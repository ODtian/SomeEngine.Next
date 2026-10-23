using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class VoxelDistanceTests
{
    [Fact]
    public async Task RebuiltDistancesKeepTheCapturedPlaneAndAnIndependentVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("varying plane scalar", new VoxelGrid(2, .5f));
        var range = new VoxelRegion(-2, -2, -1, 5, 5, 3);
        var precision = new VoxelDistancePrecision(.005f, .01f);
        var settings = new VoxelDistanceSettings(range, 4, precision);
        await field.EditCpu(new PlaneSamples(range.Expand(settings.ReadBorder))).WaitAsync();
        VoxelFieldRead source = await field.ReadCpuAsync(range);

        TerrainEdit edit = field.RebuildDistanceCpu(settings, new SubdividedVoxelDistance(9));
        Task<VoxelFieldRead> captured = field.ReadCpuAsync(range).AsTask();
        TerrainEdit later = field.EditCpu(new AddScalar(range, .25f));
        await later.WaitAsync();
        VoxelFieldRead distances = await captured;
        VoxelFieldRead current = await field.ReadCpuAsync(range);

        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
        {
            Vector3 point = field.Grid.SamplePosition(x, y, z);
            float expected = (3f * point.X + 4f * point.Y - 1f) / 5f;
            Assert.Equal(expected, distances[x, y, z], tolerance: precision.DistanceTolerance);
            Assert.Equal(expected + .25f, current[x, y, z], tolerance: precision.DistanceTolerance);
        }
        Assert.Equal(-2f, source[0, 0, 0]);
        Assert.Equal(edit.Revision, distances.Revision);
        Assert.Equal(later.Revision, current.Revision);
        Assert.Equal(0f, distances.Sample(new(1f / 3f, 0, .125f)), tolerance: precision.DistanceTolerance);
        Assert.Equal(0f, distances.Sample(new(0, .25f, .125f)), tolerance: precision.DistanceTolerance);
        float xCrossing = .5f * distances[0, 0, 0] / (distances[0, 0, 0] - distances[1, 0, 0]);
        float yCrossing = .5f * distances[0, 0, 0] / (distances[0, 0, 0] - distances[0, 1, 0]);
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
