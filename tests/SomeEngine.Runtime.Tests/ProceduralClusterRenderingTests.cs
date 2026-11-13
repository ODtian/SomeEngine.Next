using System.Numerics;
using DefaultGame;
using SomeEngine.Geometry.Cluster;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using System.Text.Json;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using Material = SomeEngine.Resources.Material;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ProceduralClusterRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task ImportedGltfMaterialRegionsMatchProceduralClusterGeometry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-gltf-regions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "regions.gltf");
            WriteTwoRegionGltf(sourcePath);
            ModelRoot model = ModelRoot.Load(sourcePath);
            await using var context = new ImportContext(directory, "regions.gltf",
                new SourceMeta { SourceGuid = SourceGuid.New() });
            var services = new ServiceCollection();
            EngineAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            foreach (AssetImporterSetCapability set in provider.GetServices<AssetImporterSetCapability>()
                .Where(set => set.Id == new Guid(GltfImporterSets.NeutralId) || set.Id == new Guid(GltfImporterSets.ClusterId)))
                foreach (AssetImporterCapability importer in provider.GetServices<AssetImporterCapability>()
                    .Where(importer => set.Importers.Contains(importer.Id) && importer.SourceType == typeof(GltfMesh)))
                    context.EnableImporter<GltfMesh, MeshAsset>(importer.Bind<GltfMesh>(provider));
            GltfMesh source = Assert.Single(model.LogicalMeshes);
            Guid id = context.GetAssetId("two-regions");
            context.AddAsset(source, new MeshAsset(id), "two-regions", id + ".mesh.asset");
            using MeshAsset imported = await context.GetAssetAsync<GltfMesh, MeshAsset>(source);
            string assetPath = Path.Combine(directory, "regions.mesh.asset");
            await imported.SaveAsync(assetPath);
            using MeshAsset reopened = await MeshAsset.OpenAsync(assetPath);
            ClusterMeshBlock pages = await reopened.GetBlockAsync<ClusterMeshBlock>();
            Assert.Equal([0, 1], pages.RegionIndices);
            SomeEngine.Resources.Mesh mesh = await pages.CreateAsync(await reopened.GetBlockAsync<MeshBlock>(), reopened.Name);
            await pages.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));

            SceneImage? loaded = null, procedural = null;
            await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
            [
                new(0, scene =>
                {
                    Material left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                    Material right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                    scene.AddEntity(mesh, [left, right], new(new Vector3(0, 0, 6), Quaternion.Identity));
                }),
                new(9, scene => scene.CaptureImage(image => loaded = image)),
            ]), "--no-taa", "--no-vsm");
            await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
            [
                new(0, scene =>
                {
                    Material left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                    Material right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                    ProceduralClusterExample.Add(scene, left, right);
                }),
                new(9, scene => scene.CaptureImage(image => procedural = image)),
            ]), "--no-taa", "--no-vsm");

            SceneImages.AssertColorRegion(loaded!, 20, 30, 4, 4, 0, 186, 0);
            SceneImages.AssertColorRegion(loaded!, 40, 30, 4, 4, 186, 0, 0);
            SceneImages.AssertEqualRegion(procedural!, loaded!, 16, 24, 32, 16);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WriteTwoRegionGltf(string path)
    {
        Vector3[] positions = [new(-2, -1, 0), new(0, -1, 0), new(-2, 1, 0),
            new(0, 1, 0), new(2, -1, 0), new(2, 1, 0)];
        using var stream = new MemoryStream();
        using var bytes = new BinaryWriter(stream);
        foreach (Vector3 position in positions) { bytes.Write(position.X); bytes.Write(position.Y); bytes.Write(position.Z); }
        foreach (Vector3 _ in positions) { bytes.Write(0f); bytes.Write(0f); bytes.Write(-1f); }
        foreach (Vector3 _ in positions) { bytes.Write(1f); bytes.Write(0f); bytes.Write(0f); bytes.Write(1f); }
        foreach (Vector3 position in positions) { bytes.Write((position.X + 2) / 4); bytes.Write((position.Y + 1) / 2); }
        foreach (uint index in new uint[] { 0, 2, 1, 1, 2, 3, 1, 3, 4, 4, 3, 5 }) bytes.Write(index);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { uri = "data:application/octet-stream;base64," + Convert.ToBase64String(stream.ToArray()), byteLength = stream.Length } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 72 },
                new { buffer = 0, byteOffset = 72, byteLength = 72 }, new { buffer = 0, byteOffset = 144, byteLength = 96 },
                new { buffer = 0, byteOffset = 240, byteLength = 48 }, new { buffer = 0, byteOffset = 288, byteLength = 24 },
                new { buffer = 0, byteOffset = 312, byteLength = 24 } },
            accessors = new object[] { new { bufferView = 0, componentType = 5126, count = 6, type = "VEC3", min = new[] { -2, -1, 0 }, max = new[] { 2, 1, 0 } },
                new { bufferView = 1, componentType = 5126, count = 6, type = "VEC3" }, new { bufferView = 2, componentType = 5126, count = 6, type = "VEC4" },
                new { bufferView = 3, componentType = 5126, count = 6, type = "VEC2" }, new { bufferView = 4, componentType = 5125, count = 6, type = "SCALAR" },
                new { bufferView = 5, componentType = 5125, count = 6, type = "SCALAR" } },
            materials = new[] { new { name = "Left" }, new { name = "Right" } },
            meshes = new[] { new { name = "Two regions", primitives = new[] {
                new { attributes = new { POSITION = 0, NORMAL = 1, TANGENT = 2, TEXCOORD_0 = 3 }, indices = 4, material = 0 },
                new { attributes = new { POSITION = 0, NORMAL = 1, TANGENT = 2, TEXCOORD_0 = 3 }, indices = 5, material = 1 } } } },
        }));
    }

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
