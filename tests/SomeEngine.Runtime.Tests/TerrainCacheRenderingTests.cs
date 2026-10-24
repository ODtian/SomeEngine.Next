using System.Numerics;
using DefaultGame;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainCacheRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task ReopenedOfflineGeometryKeepsItsPictureThroughLocalEdits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Cache-Scene-{Guid.NewGuid():N}.terrain.asset");
        Terrain? restored = null;
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var editing = new TerrainEditing(scheduler);
            using Terrain original = CreateTerrain(editing);
            using var materialAsset = await MaterialAsset.OpenAsync(Path.Combine(content.DirectoryPath, "assets/Materials/sphere-0.material.asset"));
            var grid = new TerrainChunkGrid(64, .125f);
            var identity = new TerrainGeometryIdentity("Transvoxel/1", "Cache example ground/1");
            TerrainClusterData generated = await TerrainAssetExample.SaveCacheAsync(path, original, editing,
                [grid.GetBounds(new(0, 0, 0), 0), grid.GetBounds(new(1, 0, 0), 0)], new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, identity, [materialAsset.Reference]);
            var scene = CreateScene(grid);
            int placement = 0;
            TerrainEdit edit = default;
            TerrainChunk[] beforeChunks = [], afterChunks = [];
            SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
            var application = new ApplicationScene(content.DirectoryPath)
            {
                Options = Options(),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        Material material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                        var loaded = TerrainAssetExample.OpenCacheAsync(path, editing, (id, _) =>
                        {
                            Assert.Equal(materialAsset.Reference, id);
                            return ValueTask.FromResult(material);
                        }).GetAwaiter().GetResult();
                        restored = loaded.Terrain;
                        Assert.Equal(2, scene.UseCache(restored, loaded.Cache, identity));
                        foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                        placement = scene.Add(CreatePlacement(restored, loaded.Materials));
                    }
                    if (frame.Index == 8)
                    {
                        beforeChunks = scene.GetChunks(placement);
                        frame.Scene.CaptureImage(image => before = image);
                    }
                    if (frame.Index == 9) edit = restored!.HeightFields[0].EditCpu(new HeightDeltaBrush(new(24, 8, 32, 32), 1f));
                    if (frame.Index == 10) scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                    if (frame.Index == 17)
                    {
                        afterChunks = scene.GetChunks(placement);
                        frame.Scene.CaptureImage(image => after = image);
                    }
                },
            };
            application.InstallGeometry(new TerrainGeometryFeature(scene));
            Assert.Equal(0, await Application.RunAsync([], application: application));
            Assert.All(beforeChunks, chunk => Assert.Equal(original.Revision, chunk.Revision));
            Assert.All(afterChunks, chunk => Assert.Equal(chunk.Coordinates.X == 0 ? edit.Revision : original.Revision, chunk.Revision));

            using Terrain reference = original.Duplicate();
            TerrainScene referenceScene = CreateScene(grid);
            Assert.Equal(2, referenceScene.UseCache(reference, generated, identity));
            var expected = new ApplicationScene(content.DirectoryPath)
            {
                Options = Options(),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        Material material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                        foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                        referenceScene.Add(CreatePlacement(reference, [material]));
                    }
                    if (frame.Index is 1 or 10) referenceScene.WaitForBuildsAsync().GetAwaiter().GetResult();
                    if (frame.Index == 8) frame.Scene.CaptureImage(image => expectedBefore = image);
                    if (frame.Index == 9) reference.HeightFields[0].EditCpu(new HeightDeltaBrush(new(24, 8, 32, 32), 1f));
                    if (frame.Index == 17) frame.Scene.CaptureImage(image => expectedAfter = image);
                },
            };
            expected.InstallGeometry(new TerrainGeometryFeature(referenceScene));
            Assert.Equal(0, await Application.RunAsync([], application: expected));
            SceneImages.AssertEqual(expectedBefore!, before!);
            SceneImages.AssertEqual(expectedAfter!, after!);
            SceneImages.AssertChanged(before!, after!);
        }
        finally { restored?.Dispose(); File.Delete(path); }
    }

    private static Terrain CreateTerrain(TerrainEditing editing)
    {
        var terrain = new Terrain(editing);
        HeightField height = terrain.CreateHeightField("Cached plane", new(129, 65, 16, .125f), 1f);
        terrain.Surface = new HeightSurface(height);
        return terrain;
    }

    private static TerrainScene CreateScene(TerrainChunkGrid grid) => new(
        new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(), StandardClusterVertexEncoder.Instance,
            ClusterBuildStrategy.Realtime), grid);

    private static TerrainPlacement CreatePlacement(Terrain terrain, IReadOnlyList<Material> materials) =>
        new(terrain, new(0, 0, 0, 2, 1, 1), materials)
        {
            Transform = new TransformQvvs(new Vector3(-8, -2, 6), Quaternion.Identity),
        };

    private static RuntimeStartupOptions Options() => RuntimeStartupOptions.Parse(
        ["--hidden-window", "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]);

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
