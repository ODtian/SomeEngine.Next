using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class TerrainBrushTests
{
    [Fact]
    public async Task HeightSmoothingUsesTheSameSourceNeighborhoodAcrossPages()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField field = terrain.CreateHeightField("ground", new HeightGrid(7, 7, 3));
        await field.EditCpu(new HeightDeltaBrush(new HeightRegion(3, 3, 1, 1), 9f)).WaitAsync();
        HeightFieldRead earlier = await field.ReadCpuAsync(field.Grid.Range);

        TerrainEdit edit = field.EditCpu(new HeightSmoothBrush(field.Grid, new Vector2(3, 3), 3f, 1));
        await edit.WaitAsync();
        HeightFieldRead result = await field.ReadCpuAsync(field.Grid.Range);

        // A 9 metre impulse has a one metre 3x3 mean, weighted by distance from the brush centre.
        float[,] expected =
        {
            { .5285955f, .6666667f, .5285955f },
            { .6666667f, 1f, .6666667f },
            { .5285955f, .6666667f, .5285955f },
        };
        for (int z = 0; z < 7; z++)
        for (int x = 0; x < 7; x++)
            Assert.Equal(x is >= 2 and <= 4 && z is >= 2 and <= 4 ? expected[z - 2, x - 2] : 0f,
                result[x, z], tolerance: 1e-6f);
        Assert.Equal(9f, earlier[3, 3]);
        Assert.Equal(edit.Revision, result.Revision);
    }

    [Fact]
    public async Task VoxelSmoothingUsesTheSameSourceNeighborhoodAcrossPages()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("density", new VoxelGrid(3), 0f);
        await field.EditCpu(new VoxelSphereDeltaBrush(field.Grid, Vector3.Zero, .5f, 27f)).WaitAsync();
        var brush = new VoxelSmoothBrush(field.Grid, Vector3.Zero, 3f, 1);
        VoxelRegion range = brush.Range.Expand(1);
        VoxelFieldRead earlier = await field.ReadCpuAsync(range);

        TerrainEdit edit = field.EditCpu(brush);
        await edit.WaitAsync();
        VoxelFieldRead result = await field.ReadCpuAsync(range);

        // A 27 unit impulse has a one unit 3x3x3 mean; these are the radial weights at its neighboring samples.
        float[,,] expected =
        {
            {
                { .4226497f, .5285955f, .4226497f },
                { .5285955f, .6666667f, .5285955f },
                { .4226497f, .5285955f, .4226497f },
            },
            {
                { .5285955f, .6666667f, .5285955f },
                { .6666667f, 1f, .6666667f },
                { .5285955f, .6666667f, .5285955f },
            },
            {
                { .4226497f, .5285955f, .4226497f },
                { .5285955f, .6666667f, .5285955f },
                { .4226497f, .5285955f, .4226497f },
            },
        };
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
            Assert.Equal(x is >= -1 and <= 1 && y is >= -1 and <= 1 && z is >= -1 and <= 1
                    ? expected[z + 1, y + 1, x + 1] : 0f,
                result[x, y, z], tolerance: 1e-6f);
        Assert.Equal(27f, earlier[0, 0, 0]);
        Assert.Equal(edit.Revision, result.Revision);
    }

    [Fact]
    public async Task FlattenBrushesUseMeterRadiusAndTheSameRadialStrength()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(5, 5, 2, 2f), 6f);
        VoxelField voxels = terrain.CreateVoxelField("density", new VoxelGrid(2, 2f), 6f);
        var plane = new Plane(Vector3.UnitY, -2f);
        var heightBrush = new HeightFlattenBrush(heights.Grid, new Vector2(4, 4), 4f, plane, .5f);
        var voxelBrush = new VoxelFlattenBrush(voxels.Grid, new Vector3(0, 4, 0), 4f, plane, .5f);

        await heights.EditCpu(heightBrush).WaitAsync();
        await voxels.EditCpu(voxelBrush).WaitAsync();
        HeightFieldRead height = await heights.ReadCpuAsync(heights.Grid.Range);
        VoxelFieldRead voxel = await voxels.ReadCpuAsync(voxelBrush.Range);

        Assert.Equal([4f, 5f, 6f], new[] { height[2, 2], height[3, 2], height[4, 2] });
        Assert.Equal([4f, 5f, 6f], new[] { voxel[0, 2, 0], voxel[1, 2, 0], voxel[2, 2, 0] });
        Assert.Equal(4.5f, voxel[0, 1, 0]);
        Assert.Equal(5.5f, voxel[0, 3, 0]);
    }

    [Fact]
    public async Task CustomBrushesUseCapturedGradientsInTerrainMeters()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(7, 5, 3, 2f));
        VoxelField voxels = terrain.CreateVoxelField("density", new VoxelGrid(3, 2f));
        var voxelSource = new VoxelRegion(-2, -2, -2, 5, 5, 5);
        await heights.EditCpu(new LinearHeights(heights.Grid.Range)).WaitAsync();
        await voxels.EditCpu(new LinearVoxels(voxelSource)).WaitAsync();
        HeightFieldRead earlierHeight = await heights.ReadCpuAsync(heights.Grid.Range);
        VoxelFieldRead earlierVoxel = await voxels.ReadCpuAsync(voxelSource);
        var heightRange = new HeightRegion(2, 1, 3, 3);
        var voxelRange = new VoxelRegion(-1, -1, -1, 3, 3, 3);

        await heights.EditCpu(new HeightGradientSquaredBrush(heightRange)).WaitAsync();
        await voxels.EditCpu(new VoxelGradientSquaredBrush(voxelRange)).WaitAsync();
        HeightFieldRead height = await heights.ReadCpuAsync(heightRange);
        VoxelFieldRead voxel = await voxels.ReadCpuAsync(voxelRange);

        for (int z = heightRange.MinZ; z < heightRange.MaxZ; z++)
        for (int x = heightRange.MinX; x < heightRange.MaxX; x++)
            Assert.Equal(13f, height[x, z]);
        for (int z = voxelRange.MinZ; z < voxelRange.MaxZ; z++)
        for (int y = voxelRange.MinY; y < voxelRange.MaxY; y++)
        for (int x = voxelRange.MinX; x < voxelRange.MaxX; x++)
            Assert.Equal(29f, voxel[x, y, z]);
        Assert.Equal(new Vector2(2, 3), earlierHeight.GetGradient(3, 2));
        Assert.Equal(new Vector3(2, 3, -4), earlierVoxel.GetGradient(0, 0, 0));
        Assert.Equal(Vector2.Zero, height.GetGradient(3, 2));
        Assert.Equal(Vector3.Zero, voxel.GetGradient(0, 0, 0));
    }

    private readonly record struct LinearHeights(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z)
        {
            Vector2 position = source.Grid.SamplePosition(x, z);
            return 2f * position.X + 3f * position.Y;
        }
    }

    private readonly record struct LinearVoxels(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z)
        {
            Vector3 position = source.Grid.SamplePosition(x, y, z);
            return 2f * position.X + 3f * position.Y - 4f * position.Z;
        }
    }

    private readonly record struct HeightGradientSquaredBrush(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 1;
        public float Apply(HeightFieldRead source, int x, int z) => source.GetGradient(x, z).LengthSquared();
    }

    private readonly record struct VoxelGradientSquaredBrush(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 1;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source.GetGradient(x, y, z).LengthSquared();
    }
}
