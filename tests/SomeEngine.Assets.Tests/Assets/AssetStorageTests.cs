using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetStorageTests
{
    [Fact]
    public void CreateImporters_ExposesAuthoringPipelineTypes()
    {
        IReadOnlyList<IAssetImporter> importers = [new GltfSourceImporter(), new SlangSourceImporter()];

        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.SlangShaderImporter));
        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.GltfSourceImporter));
    }

    [Fact]
    public void AssetRuntimeAssemblyDoesNotReferenceImporterToolchains()
    {
        string[] references = typeof(Texture.Asset).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, static name => name.StartsWith("SharpGLTF", StringComparison.Ordinal));
        Assert.Contains(references, static name =>
            string.Equals(name, "SlangShaderSharp", StringComparison.Ordinal));
        Assert.DoesNotContain(references, static name => name.Contains("MeshOptimizer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateProjectImportsAndPublishesOneLooseStorageContract()
    {
        string dir = CreateTempDir();

        try
        {
            Guid materialGuid = Guid.NewGuid();
            string materialPath = Path.Combine(dir, "assets", "Materials", "generated.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
                Name = "GeneratedMaterial",
            })).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            using var materialFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/generated.material.asset"));
            project.RegisterAsset(materialFile.Describe());
            IAssetStorage storage = project.CreateStorage();
            Assert.True(storage.TryFind(materialGuid, out AssetEntry entry));
            await using var loader = new AssetLoader(storage);
            using Material.Asset file = await Material.Asset.OpenAsync(loader, new(entry.AssetGuid));

            Assert.Equal(materialGuid, file.Id.Value);
            Assert.Equal(materialGuid, project.Resolve("assets/Materials/generated.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
