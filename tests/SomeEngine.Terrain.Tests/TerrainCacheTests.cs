using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
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
            TerrainClusterData authored;
            MeshClusterGroup[][] authoredGroups;
            using (TerrainClusterBlock clusters = await TerrainClusterCache.BuildAsync(captured, editing,
                [grid.GetBounds(new(0, 0, 0), 0), grid.GetBounds(new(1, 0, 0), 0)], new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, identity))
            using (var asset = new TerrainAsset { Name = "cached ground" })
            {
                authoredGroups = Enumerable.Range(0, clusters.Entries.Count).Select(index =>
                    clusters.Groups.Where(group => group.EntryIndex == index).Select(static group => group.Group).ToArray()).ToArray();
                Assert.Equal(2, authoredGroups.Length);
                Assert.All(clusters.Groups, group => Assert.InRange(group.EntryIndex, 0, 1));
                Assert.All(authoredGroups, groups => Assert.NotEmpty(groups));
                Assert.Contains(authoredGroups.SelectMany(static groups => groups), group => !group.Parents.IsEmpty);
                authored = await clusters.CreateAsync();
                Assert.Equal(authoredGroups.Length, authored.Meshes.Count);
                for (int index = 0; index < authoredGroups.Length; index++)
                    AssertClusterGroups(authoredGroups[index], Assert.IsType<Mesh>(authored.Meshes[index].Mesh));
                await asset.UpdateAsync(captured, []);
                asset.SetBlock(clusters);
                await asset.SaveAsync(path);
            }

            using TerrainAsset opened = await TerrainAsset.OpenAsync(path);
            using Resources.Terrain restored = await opened.CreateTerrainAsync(editing);
            TerrainClusterData cached = await (await opened.GetBlockAsync<TerrainClusterBlock>()).CreateAsync();
            opened.Dispose();
            Assert.Equal(authored.Meshes.Count, cached.Meshes.Count);
            for (int index = 0; index < authored.Meshes.Count; index++)
            {
                Assert.Equal(authored.Meshes[index].Entry.X, cached.Meshes[index].Entry.X);
                Mesh expectedMesh = Assert.IsType<Mesh>(authored.Meshes[index].Mesh);
                Mesh reopenedMesh = Assert.IsType<Mesh>(cached.Meshes[index].Mesh);
                AssertClusterGroups(authoredGroups[index], reopenedMesh);
                Assert.Equal(expectedMesh.Bvh.ToArray(), reopenedMesh.Bvh.ToArray());
                Assert.Equal(expectedMesh.LowestDetailPageIndices.ToArray(), reopenedMesh.LowestDetailPageIndices.ToArray());
            }
            var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
            var scene = new TerrainScene(adapter, grid);
            Assert.Equal(2, scene.UseCache(restored, cached, identity));
            var material = new UnlitMaterial();
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
            Assert.NotEmpty(mesh.ClusterGroups);
            var next = new TerrainScene(adapter, grid);
            Assert.Equal(1, next.UseCache(restored, cached, identity));
        }
        finally { File.Delete(path); }
    }

    private static void AssertClusterGroups(IReadOnlyList<MeshClusterGroup> expected, Mesh mesh)
    {
        Assert.Equal(expected.Count, mesh.ClusterGroups.Length);
        for (int index = 0; index < expected.Count; index++)
        {
            MeshClusterGroup actual = mesh.ClusterGroups[index];
            Assert.Equal(expected[index].FirstPage, actual.FirstPage);
            Assert.Equal(expected[index].PageCount, actual.PageCount);
            Assert.Equal(expected[index].HierarchyParts.ToArray(), actual.HierarchyParts.ToArray());
            Assert.Equal(expected[index].Parents.ToArray(), actual.Parents.ToArray());
        }
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
