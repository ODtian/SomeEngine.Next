using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainCacheTests
{
    [Fact]
    public async Task SavedOfflineChunksStartReadyAndALocalEditReplacesItsGeometry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Cache-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            using var terrain = new Resources.Terrain(editing);
            HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(129, 65, 16, .25f));
            heights.EditCpu(new GroundHeights(heights.Grid.Range));
            terrain.Surface = new HeightSurface(heights);
            TerrainCpuData captured = await terrain.CaptureCpuAsync();
            var grid = new TerrainChunkGrid(64, .25f);
            var identity = new TerrainGeometryIdentity("Transvoxel/1", "Cache ground attributes/1");
            using (TerrainClusterBlock clusters = await TerrainClusterCache.BuildAsync(captured, editing,
                [grid.GetBounds(new(0, 0, 0), 0), grid.GetBounds(new(1, 0, 0), 0)], new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, identity))
            using (var asset = new TerrainAsset { Name = "cached ground" })
            {
                await asset.UpdateAsync(captured, []);
                asset.SetBlock(clusters);
                await asset.SaveAsync(path);
            }

            using TerrainAsset opened = await TerrainAsset.OpenAsync(path);
            using Resources.Terrain restored = await opened.CreateTerrainAsync(editing);
            TerrainClusterData cached = await (await opened.GetBlockAsync<TerrainClusterBlock>()).CreateAsync();
            opened.Dispose();
            var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
            var scene = new TerrainScene(adapter, grid);
            Assert.Equal(2, scene.UseCache(restored, cached, identity));
            using var material = new UnlitMaterial();
            int placement = scene.Add(new TerrainPlacement(restored, new(0, 0, 0, 2, 1, 1), [material]));
            using var world = new World();
            using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            systems.Add(new TerrainCommitSystem(scene));
            systems.Add(new TerrainLodSystem(scene));
            systems.Add(new TerrainMeshSystem(scene));
            systems.Update();
            TerrainChunk[] before = scene.GetChunks(placement);
            Assert.All(before, chunk => Assert.Equal(captured.Revision, chunk.Revision));
            Mesh mesh = scene.GetGeometry(before.Single(chunk => chunk.Coordinates.X == 0).GeometryId)!;
            Assert.True(mesh.PageCount > mesh.LowestDetailPageIndices.Length);
            Assert.All(mesh.LowestDetailPageIndices, page => Assert.True(mesh.TryGetPage((int)page, out _)));
            Mesh initial = mesh.CaptureData();

            TerrainEdit edit = restored.HeightFields[0].EditCpu(new HeightDeltaBrush(new(4, 4, 8, 8), 1f));
            systems.Update();
            await scene.WaitForBuildsAsync();
            systems.Update();
            Assert.All(scene.GetChunks(placement), chunk =>
                Assert.Equal(chunk.Coordinates.X == 0 ? edit.Revision : captured.Revision, chunk.Revision));
            Assert.True(mesh.Revision > initial.Revision);
            var next = new TerrainScene(adapter, grid);
            Assert.Equal(1, next.UseCache(restored, cached, identity));
        }
        finally { File.Delete(path); }
    }

    private readonly record struct GroundHeights(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) =>
            3f + MathF.Sin(x * .17f) * MathF.Cos(z * .13f);
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
