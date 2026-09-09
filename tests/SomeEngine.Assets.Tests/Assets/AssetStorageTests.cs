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
        string[] references = typeof(TextureData).Assembly
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
            AssetWriter.Write<Material>((new StandardMaterialData
            {
                AssetGuid = materialGuid.ToString(),
                Name = "GeneratedMaterial",
            }).ToAsset(), materialPath);

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            await project.RegisterAssetAsync<Material>(
                "assets/Materials/generated.material.asset");
            IAssetStorage storage = project.CreateStorage();
            Assert.True(storage.TryFind(materialGuid, out AssetEntry entry));
            await using BinaryDocument<MaterialData> document =
                await AssetTestFiles.OpenAsync<Material, MaterialData>(storage, entry);

            Assert.Equal(materialGuid.ToString(), document.Root.AssetGuid);
            Assert.Equal(materialGuid, project.Resolve("assets/Materials/generated.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
