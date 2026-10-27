using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainSceneTests
{
    [Fact]
    public async Task ScenesInOneWorldKeepTheirChunksAndEditsSeparate()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var material = new UnlitMaterial();
        using var firstTerrain = new Resources.Terrain(new TerrainEditing(scheduler));
        using var secondTerrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField firstHeight = firstTerrain.CreateHeightField("first", new(5, 5, 4), .5f);
        HeightField secondHeight = secondTerrain.CreateHeightField("second", new(5, 5, 4), 2.5f);
        firstTerrain.Surface = new HeightSurface(firstHeight);
        secondTerrain.Surface = new HeightSurface(secondHeight);
        var first = new TerrainScene(new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime), new(4, 1f));
        var second = new TerrainScene(new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime), new(4, 1f));
        int firstPlacement = first.Add(new(firstTerrain, new(0, 0, 0, 1, 1, 1), [material]));
        int secondPlacement = second.Add(new(secondTerrain, new(0, 0, 0, 1, 1, 1), [material]));
        systems.Add(new TerrainCommitSystem(first));
        systems.Add(new TerrainLodSystem(first));
        systems.Add(new TerrainMeshSystem(first));
        systems.Add(new TerrainCommitSystem(second));
        systems.Add(new TerrainLodSystem(second));
        systems.Add(new TerrainMeshSystem(second));
        systems.Update();
        await first.WaitForBuildsAsync();
        await second.WaitForBuildsAsync();
        systems.Update();
        Assert.Single(first.GetChunks(firstPlacement));
        TerrainChunk secondBefore = Assert.Single(second.GetChunks(secondPlacement));

        TerrainEdit edit = firstHeight.EditCpu(new HeightDeltaBrush(firstHeight.Grid.Range, 1f));
        systems.Update();
        await first.WaitForBuildsAsync();
        systems.Update();

        TerrainChunk firstAfter = Assert.Single(first.GetChunks(firstPlacement));
        Assert.Equal(edit.Revision, firstAfter.Revision);
        Assert.Equal(secondBefore, Assert.Single(second.GetChunks(secondPlacement)));
        Assert.Equal(1.5f, first.GetGeometry(firstAfter.GeometryId)!.Bounds!.Center.Y);
        Assert.Equal(2.5f, second.GetGeometry(secondBefore.GeometryId)!.Bounds!.Center.Y);
    }

    [Fact]
    public async Task BlendSourceEditsUpdateChunksWithinEachFieldsSamplingNeighborhood()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var material = new UnlitMaterial();
        var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(13, 5, 4, 2f), 4f);
        VoxelField voxels = terrain.CreateVoxelField("detail", new VoxelGrid(4, 2f));
        terrain.Surface = new BlendSurface(new HeightSurface(heights), new VoxelSurface(voxels), .5f);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        var scene = new TerrainScene(adapter, new TerrainChunkGrid(8, 1f));
        int placement = scene.Add(new TerrainPlacement(terrain, new(0, 0, 0, 3, 1, 1), [material]));
        systems.Add(new TerrainCommitSystem(scene));
        systems.Add(new TerrainLodSystem(scene));
        systems.Add(new TerrainMeshSystem(scene));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        var before = scene.GetChunks(placement).ToDictionary(chunk => chunk.Coordinates);

        TerrainEdit heightEdit = heights.EditCpu(new HeightDeltaBrush(new(5, 2, 1, 1), 1f));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        var afterHeight = scene.GetChunks(placement).ToDictionary(chunk => chunk.Coordinates);
        foreach (TerrainChunk chunk in afterHeight.Values)
            Assert.Equal(chunk.Coordinates.X < 2 ? heightEdit.Revision : before[chunk.Coordinates].Revision, chunk.Revision);

        TerrainEdit voxelEdit = voxels.EditCpu(new SolidSample(new(10, 2, 2, 1, 1, 1)));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        foreach (TerrainChunk chunk in scene.GetChunks(placement))
        {
            Assert.Equal(chunk.Coordinates.X == 2 ? voxelEdit.Revision : afterHeight[chunk.Coordinates].Revision, chunk.Revision);
            Assert.NotNull(scene.GetGeometry(chunk.GeometryId));
        }
    }

    [Fact]
    public async Task VoxelEditUpdatesChunksWithinItsSamplingNeighborhood()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var material = new UnlitMaterial();
        var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        VoxelField voxels = terrain.CreateVoxelField("solid", new VoxelGrid(4, 2f));
        terrain.Surface = new VoxelSurface(voxels);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        var scene = new TerrainScene(adapter, new TerrainChunkGrid(8, 1f));
        int placement = scene.Add(new TerrainPlacement(terrain, new(0, 0, 0, 2, 2, 2), [material]));
        systems.Add(new TerrainCommitSystem(scene));
        systems.Add(new TerrainLodSystem(scene));
        systems.Add(new TerrainMeshSystem(scene));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        var before = scene.GetChunks(placement).ToDictionary(chunk => chunk.Coordinates);

        TerrainEdit edit = voxels.EditCpu(new SolidSample(new(5, 2, 2, 1, 1, 1)));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();

        foreach (TerrainChunk chunk in scene.GetChunks(placement))
            Assert.Equal(chunk.Coordinates.Y == 0 && chunk.Coordinates.Z == 0
                ? edit.Revision : before[chunk.Coordinates].Revision, chunk.Revision);
        TerrainChunk solid = scene.GetChunks(placement).Single(chunk => chunk.Coordinates == new TerrainChunkCoordinate(1, 0, 0));
        Assert.NotNull(scene.GetGeometry(solid.GeometryId));
        VoxelFieldRead samples = await voxels.ReadCpuAsync(new(5, 2, 2, 1, 1, 1));
        Assert.Equal(-1f, samples[5, 2, 2]);
    }

    [Fact]
    public async Task HeightEditUpdatesAffectedChunksInBothSharedPlacements()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var material = new UnlitMaterial();
        var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(9, 5, 4));
        terrain.Surface = new HeightSurface(heights);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        var scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1f));
        var chunks = new TerrainChunkRange(0, -1, 0, 2, 2, 1);
        int first = scene.Add(new TerrainPlacement(terrain, chunks, [material]));
        int second = scene.Add(new TerrainPlacement(terrain, chunks, [material])
        {
            Transform = new TransformQvvs(new Vector3(20, 0, 0), Quaternion.Identity),
        });
        systems.Add(new TerrainCommitSystem(scene));
        systems.Add(new TerrainLodSystem(scene));
        systems.Add(new TerrainMeshSystem(scene));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        var before = scene.GetChunks(first).ToDictionary(chunk => chunk.Coordinates);

        TerrainEdit edit = heights.EditCpu(new HeightDeltaBrush(new HeightRegion(1, 1, 1, 1), 1f));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();

        foreach (int placement in new[] { first, second })
        foreach (TerrainChunk chunk in scene.GetChunks(placement))
            Assert.Equal(chunk.Coordinates.X == 0 ? edit.Revision : before[chunk.Coordinates].Revision, chunk.Revision);
        HeightFieldRead samples = await heights.ReadCpuAsync(heights.Grid.Range);
        Assert.Equal(1f, samples[1, 1]);
    }

    private sealed class SurfaceAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }

    private readonly record struct SolidSample(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => -1f;
    }
}
