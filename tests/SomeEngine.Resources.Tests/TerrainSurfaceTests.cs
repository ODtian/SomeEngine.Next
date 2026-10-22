using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources.Tests;

public sealed class TerrainSurfaceTests
{
    [Fact]
    public async Task HeightSurfaceReadsSignedValuesFromOneCapturedVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(4, 4, 2), 1f);
        terrain.Surface = new HeightSurface(heights);
        var bounds = new TerrainBounds(Vector3.Zero, new Vector3(3));
        TerrainSurfaceRead earlier = await terrain.Surface.ReadCpuAsync(bounds);

        TerrainEdit edit = heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 2f));
        await edit.WaitAsync();
        TerrainSurfaceRead current = await terrain.Surface.ReadCpuAsync(bounds);

        Assert.Equal(1f, earlier.Sample(new(1.5f, 2f, 1.5f)));
        Assert.Equal(-1f, current.Sample(new(1.5f, 2f, 1.5f)));
        Assert.Equal(0f, current.Sample(new(1.5f, 3f, 1.5f)));
        Assert.Equal(Vector3.UnitY, current.GetNormal(new(1.5f, 3f, 1.5f), 1f));
        Assert.Equal(edit.Revision, current.Revision);
    }

    [Fact]
    public async Task VoxelSurfaceKeepsItsCapturedScalarVersionAcrossLaterEdits()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField voxels = terrain.CreateVoxelField("solid", new VoxelGrid(4));
        terrain.Surface = new VoxelSurface(voxels);
        var bounds = new TerrainBounds(new Vector3(-2), new Vector3(2));
        TerrainEdit first = voxels.EditCpu(new VoxelSphereDeltaBrush(voxels.Grid, Vector3.Zero, 2f, -2f));
        ValueTask<TerrainSurfaceRead> earlierRequest = terrain.Surface.ReadCpuAsync(bounds);

        TerrainEdit edit = voxels.EditCpu(new VoxelSphereDeltaBrush(voxels.Grid, Vector3.Zero, 2f, 2f));
        TerrainSurfaceRead earlier = await earlierRequest;
        TerrainSurfaceRead current = await terrain.Surface.ReadCpuAsync(bounds);

        Assert.Equal(-1f, earlier.Sample(Vector3.Zero));
        Assert.Equal(-.5f, earlier.Sample(new(.5f, 0, 0)));
        Assert.Equal(Vector3.UnitX, earlier.GetNormal(Vector3.UnitX, 1f));
        Assert.Equal(1f, current.Sample(Vector3.Zero));
        Assert.Equal(first.Revision, earlier.Revision);
        Assert.Equal(edit.Revision, current.Revision);
    }
}
