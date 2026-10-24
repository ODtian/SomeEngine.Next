using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainMesherTests
{
    [Fact]
    public async Task HeightSurfaceProducesAnUpwardFacingPlaneAtItsSampledHeight()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("plane", new HeightGrid(3, 3, 2), .5f);
        terrain.Surface = new HeightSurface(heights);
        TerrainSurfaceRead surface = await terrain.Surface.ReadCpuAsync(new(Vector3.Zero, new Vector3(2)));

        TerrainSurfaceMesh mesh = new MarchingCubesTerrainMesher().Build(surface,
            new TerrainChunkBounds(new(0, 0, 0), 2, 1f));

        Assert.All(mesh.Positions.ToArray(), point => Assert.Equal(.5f, point.Y));
        float area = 0;
        ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            Vector3 a = positions[(int)indices[triangle]];
            Vector3 b = positions[(int)indices[triangle + 1]];
            Vector3 c = positions[(int)indices[triangle + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(normal, Vector3.UnitY) > 0);
            area += normal.Length() * .5f;
        }
        Assert.Equal(4f, area);
    }
}
