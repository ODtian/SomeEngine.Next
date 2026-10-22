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
    public async Task VoxelEditUpdatesTheSpatialChunkContainingItsSamples()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        using var material = new UnlitMaterial();
        var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        VoxelField voxels = terrain.CreateVoxelField("solid", new VoxelGrid(4));
        terrain.Surface = new VoxelSurface(voxels);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        var scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1f));
        int placement = scene.Add(new TerrainPlacement(terrain, new(-1, -1, -1, 2, 2, 2), [material]));
        systems.Add(new TerrainCommitSystem(scene));
        systems.Add(new TerrainLodSystem(scene));
        systems.Add(new TerrainMeshSystem(scene));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        var before = scene.GetChunks(placement).ToDictionary(chunk => chunk.Coordinates);

        TerrainEdit edit = voxels.EditCpu(new SolidSample(new(2, 2, 2, 1, 1, 1)));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();

        foreach (TerrainChunk chunk in scene.GetChunks(placement))
            Assert.Equal(chunk.Coordinates == new TerrainChunkCoordinate(0, 0, 0)
                ? edit.Revision : before[chunk.Coordinates].Revision, chunk.Revision);
        TerrainChunk solid = scene.GetChunks(placement).Single(chunk => chunk.Coordinates == new TerrainChunkCoordinate(0, 0, 0));
        Assert.NotNull(scene.GetGeometry(solid.GeometryId));
        VoxelFieldRead samples = await voxels.ReadCpuAsync(new(2, 2, 2, 1, 1, 1));
        Assert.Equal(-1f, samples[2, 2, 2]);
    }

    [Fact]
    public async Task HeightEditUpdatesAffectedChunksInBothSharedPlacements()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        using var material = new UnlitMaterial();
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
