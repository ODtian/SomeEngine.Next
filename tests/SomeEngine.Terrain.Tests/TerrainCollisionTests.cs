using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainCollisionTests
{
    [Fact]
    public async Task CapturedQueriesAndCollisionMeshesKeepTheSameSurfaceVersions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(9, 9, 3, .5f), .25f);
        VoxelField voxels = terrain.CreateVoxelField("distance", new VoxelGrid(3, .5f));
        var voxelRange = new VoxelRegion(-2, -3, -2, 11, 10, 9);
        await voxels.EditCpu(new PlaneSamples(voxelRange, 1.25f)).WaitAsync();
        var heightSurface = new HeightSurface(heights);
        var voxelSurface = new VoxelSurface(voxels);
        var blend = new BlendSurface(heightSurface, voxelSurface, .5f);
        terrain.Surface = blend;
        ulong revision = terrain.Revision, heightRevision = heights.Revision, voxelRevision = voxels.Revision;
        var bounds = new TerrainBounds(new(.35f, -.4f, .2f), new(2.45f, 1.6f, 1.7f));
        var grid = new TerrainCollisionGrid(bounds, 7, 4, 3);
        Task<HeightFieldRead> heightRead = heights.ReadCpuAsync(heights.Grid.Range).AsTask();
        Task<VoxelFieldRead> voxelRead = voxels.ReadCpuAsync(voxelRange).AsTask();
        Task<TerrainCollisionMesh> heightMesh = heightSurface.BuildCollisionMeshAsync(grid).AsTask();
        Task<TerrainCollisionMesh> voxelMesh = voxelSurface.BuildCollisionMeshAsync(grid).AsTask();
        Task<TerrainCollisionMesh> blendMesh = blend.BuildCollisionMeshAsync(grid).AsTask();

        await heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1f)).WaitAsync();
        await voxels.EditCpu(new AddScalar(voxelRange, .5f)).WaitAsync();
        TerrainHeightQuery height = (await heightRead).QueryHeight(new(1.4f, .95f), .1f);
        TerrainDistanceQuery distance = (await voxelRead).QueryDistance(new(1.4f, 1.5f, .95f), .1f);
        TerrainCollisionMesh capturedHeight = await heightMesh;
        TerrainCollisionMesh capturedVoxel = await voxelMesh;
        TerrainCollisionMesh capturedBlend = await blendMesh;
        TerrainSurfaceQuery surface = capturedBlend.Source.Query(new(1.4f, 1.5f, .95f), .1f);

        Assert.Equal(.25f, height.Height);
        Assert.Equal(.25f, distance.Distance);
        Assert.Equal(.75f, surface.Value);
        Assert.Equal(Vector3.UnitY, height.Normal);
        Assert.Equal(Vector3.UnitY, distance.Normal);
        Assert.Equal(Vector3.UnitY, surface.Normal);
        Assert.Equal(new(TerrainFieldKind.Height, heights.Id, heightRevision), height.FieldVersion);
        Assert.Equal(new(TerrainFieldKind.Voxel, voxels.Id, voxelRevision), distance.FieldVersion);
        Assert.Equal(revision, surface.Revision);
        Assert.Equal([height.FieldVersion], capturedHeight.Source.FieldVersions.ToArray());
        Assert.Equal([distance.FieldVersion], capturedVoxel.Source.FieldVersions.ToArray());
        Assert.Equal([height.FieldVersion, distance.FieldVersion], surface.FieldVersions.ToArray());
        foreach ((TerrainCollisionMesh mesh, float level) in new[]
                 { (capturedHeight, .25f), (capturedVoxel, 1.25f), (capturedBlend, .75f) })
        {
            Assert.Equal(grid, mesh.Grid);
            Assert.Equal(revision, mesh.Source.Revision);
            Assert.Equal(126, mesh.Indices.Length);
            Vector3[] positions = mesh.Positions.ToArray();
            Assert.Equal(.35f, positions.Min(point => point.X), tolerance: .00001f);
            Assert.Equal(2.45f, positions.Max(point => point.X), tolerance: .00001f);
            Assert.Equal(.2f, positions.Min(point => point.Z), tolerance: .00001f);
            Assert.Equal(1.7f, positions.Max(point => point.Z), tolerance: .00001f);
            Assert.All(positions, point =>
            {
                Assert.Equal(level, point.Y, tolerance: .00001f);
                Assert.Equal(0f, mesh.Source.Query(point, .1f).Value, tolerance: .00001f);
            });
            float area = 0;
            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                Vector3 normal = Vector3.Cross(positions[(int)indices[triangle + 1]] - positions[(int)indices[triangle]],
                    positions[(int)indices[triangle + 2]] - positions[(int)indices[triangle]]);
                Assert.True(normal.Y > 0);
                area += normal.Length() * .5f;
            }
            Assert.Equal(3.15f, area, tolerance: .00001f);
        }
        Assert.Equal(1.25f, (await heights.ReadCpuAsync(heights.Grid.Range)).QueryHeight(height.Position, .1f).Height);
        Assert.Equal(.75f, (await voxels.ReadCpuAsync(voxelRange)).QueryDistance(distance.Position, .1f).Distance);
    }

    private readonly record struct PlaneSamples(VoxelRegion Range, float Height) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source.Grid.SamplePosition(x, y, z).Y - Height;
    }

    private readonly record struct AddScalar(VoxelRegion Range, float Delta) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] + Delta;
    }
}
