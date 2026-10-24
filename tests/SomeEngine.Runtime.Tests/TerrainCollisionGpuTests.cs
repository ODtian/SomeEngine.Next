using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainCollisionGpuTests(SceneContentFixture content)
{
    [Fact]
    public async Task CollisionAndQueriesReadTheCapturedGpuDistanceWhileTheFieldContinuesEditing()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("GPU distance", new VoxelGrid(2, .5f));
        var surface = new VoxelSurface(field);
        terrain.Surface = surface;
        var range = new VoxelRegion(-2, -2, -2, 5, 5, 5);
        var settings = new VoxelDistanceSettings(range, 3, new(.005f, .01f));
        await field.EditCpu(new PlaneSamples(settings.ReadRegion)).WaitAsync();
        Shader shader = await TerrainGpuEditingTests.LoadNeighborhoodShaderAsync("terrain_voxel_distance.slang");
        TerrainEdit edit = field.RebuildDistanceGpu(settings, new SubdividedVoxelDistanceGpu(shader, 9));
        var bounds = new TerrainBounds(new(-.75f, -.25f, -.4f), new(.75f, 1.25f, .6f));
        var grid = new TerrainCollisionGrid(bounds, 3, 3, 2);
        Task<VoxelFieldRead> read = field.ReadCpuAsync(range).AsTask();
        Task<TerrainCollisionMesh> build = surface.BuildCollisionMeshAsync(grid).AsTask();
        TerrainEdit later = field.EditCpu(new AddScalar(range, .25f));
        Task<VoxelFieldRead> latest = field.ReadCpuAsync(range).AsTask();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "16"]),
        };
        application.InstallGeometry(new TerrainGpuEditingTests.FieldFeature(terrain));

        Assert.Equal(0, await Application.RunAsync([], application: application));
        TerrainCollisionMesh mesh = await build;
        TerrainDistanceQuery distance = (await read).QueryDistance(new(0, .75f, 0), .1f);
        TerrainDistanceQuery current = (await latest).QueryDistance(distance.Position, .1f);

        Assert.Equal(new(TerrainFieldKind.Voxel, field.Id, edit.Revision), distance.FieldVersion);
        Assert.Equal(new(TerrainFieldKind.Voxel, field.Id, later.Revision), current.FieldVersion);
        Assert.Equal([distance.FieldVersion], mesh.Source.FieldVersions.ToArray());
        Assert.Equal(edit.Revision, mesh.Source.Revision);
        Assert.Equal(grid, mesh.Grid);
        Assert.Equal(grid.GetReadBounds(1), mesh.Source.Bounds);
        Assert.Equal(.25f, distance.Distance, tolerance: settings.Precision.DistanceTolerance);
        Assert.Equal(.5f, current.Distance, tolerance: settings.Precision.DistanceTolerance);
        Assert.Equal(Vector3.UnitY, distance.Normal);
        Assert.Equal(36, mesh.Indices.Length);
        Vector3[] positions = mesh.Positions.ToArray();
        Assert.Equal(-.75f, positions.Min(point => point.X), tolerance: .00001f);
        Assert.Equal(.75f, positions.Max(point => point.X), tolerance: .00001f);
        Assert.Equal(-.4f, positions.Min(point => point.Z), tolerance: .00001f);
        Assert.Equal(.6f, positions.Max(point => point.Z), tolerance: .00001f);
        Assert.All(positions, point =>
        {
            TerrainSurfaceQuery query = mesh.Source.Query(point, .1f);
            Assert.Equal(.5f, point.Y, tolerance: settings.Precision.SurfaceTolerance);
            Assert.Equal(0f, query.Value, tolerance: settings.Precision.DistanceTolerance);
            Assert.Equal(Vector3.UnitY, query.Normal);
            Assert.Equal(edit.Revision, query.Revision);
        });
    }

    private readonly record struct PlaneSamples(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => 3f * (source.Grid.SamplePosition(x, y, z).Y - .5f);
    }

    private readonly record struct AddScalar(VoxelRegion Range, float Delta) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] + Delta;
    }
}
