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
public sealed class TerrainAssetRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task ReopenedTerrainRendersItsSavedSurfaceAndAcceptsFurtherEdits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Terrain-Scene-{Guid.NewGuid():N}.terrain.asset");
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            using var materialAsset = await MaterialAsset.OpenAsync(Path.Combine(content.DirectoryPath, "assets/Materials/sphere-0.material.asset"));
            var original = new BlendTerrainExample(scheduler);
            original.RaiseDetail(2f);
            ValueTask save = TerrainAssetExample.SaveAsync(path, original.Voxels.Terrain, [materialAsset.Reference]);
            original.RaiseDetail(2f);
            await save;
            var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
            var scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
            Terrain? restored = null;
            VoxelFieldRead? savedValues = null;
            TerrainEdit edit = default;
            SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
            var application = new ApplicationScene(content.DirectoryPath)
            {
                Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                        var loaded = TerrainAssetExample.OpenAsync(path, new TerrainEditing(scheduler), (id, _) =>
                        {
                            Assert.Equal(materialAsset.Reference, id);
                            return ValueTask.FromResult(material);
                        }).GetAwaiter().GetResult();
                        restored = loaded.Terrain;
                        savedValues = restored.VoxelFields[0].ReadCpuAsync(new(4, 2, 4, 1, 1, 1)).GetAwaiter().GetResult();
                        foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                        scene.Add(new TerrainPlacement(restored, new(0, -1, 0, 2, 2, 2), loaded.Materials)
                        {
                            Transform = new TransformQvvs(new Vector3(-4, -2, 6), Quaternion.Identity),
                        });
                    }
                    if (frame.Index is 1 or 10) scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                    if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                    if (frame.Index == 9) edit = restored!.VoxelFields[0].EditCpu(new VoxelSphereDeltaBrush(
                        restored.VoxelFields[0].Grid, new(4, 2, 4), 2f, -1f));
                    if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
                },
            };
            application.InstallGeometry(new TerrainGeometryFeature(scene));
            Assert.Equal(0, await Application.RunAsync([], application: application));
            VoxelFieldRead originalValues = await original.Voxels.ReadCpuAsync(new(4, 2, 4, 1, 1, 1));
            VoxelFieldRead changed = await restored!.VoxelFields[0].ReadCpuAsync(new(4, 2, 4, 1, 1, 1));
            Assert.Equal(-2f, originalValues[4, 2, 4]);
            Assert.Equal(0f, savedValues![4, 2, 4]);
            Assert.Equal(-1f, changed[4, 2, 4]);
            Assert.Equal(edit.Revision, changed.Revision);

            var reference = new BlendTerrainExample(scheduler);
            reference.RaiseDetail(2f);
            var expected = new ApplicationScene(content.DirectoryPath)
            {
                Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                        foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                        reference.AddPlacement(material);
                    }
                    if (frame.Index is 1 or 10) reference.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                    if (frame.Index == 8) frame.Scene.CaptureImage(image => expectedBefore = image);
                    if (frame.Index == 9) reference.Voxels.EditCpu(new VoxelSphereDeltaBrush(reference.Voxels.Grid, new(4, 2, 4), 2f, -1f));
                    if (frame.Index == 17) frame.Scene.CaptureImage(image => expectedAfter = image);
                },
            };
            reference.Install(expected);
            Assert.Equal(0, await Application.RunAsync([], application: expected));
            SceneImages.AssertEqual(expectedBefore!, before!);
            SceneImages.AssertEqual(expectedAfter!, after!);
            SceneImages.AssertChanged(before!, after!);
        }
        finally { File.Delete(path); }
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
