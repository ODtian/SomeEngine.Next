using System.Numerics;
using DefaultGame;
using SomeEngine.Geometry.Cluster;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ProceduralClusterRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task RealtimeAndOfflineGeometryDisplayTheSameMaterialRegions()
    {
        SceneImage? realtime = null, offline = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                ProceduralClusterExample.Add(scene, left, right);
            }),
            new(9, scene => scene.CaptureImage(image => realtime = image)),
        ]), "--no-taa", "--no-vsm");

        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(ProceduralClusterExample.CreateMesh(ClusterBuildStrategy.Offline), [left, right],
                    new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(9, scene => scene.CaptureImage(image => offline = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertColorRegion(realtime!, 20, 30, 4, 4, 0, 186, 0);
        SceneImages.AssertColorRegion(realtime!, 40, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertEqual(offline!, realtime!);
    }
}
