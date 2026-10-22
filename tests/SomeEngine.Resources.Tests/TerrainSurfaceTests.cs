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
}
