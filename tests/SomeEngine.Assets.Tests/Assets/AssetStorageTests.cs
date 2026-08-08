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
        IReadOnlyList<IAssetImporter> importers = AssetAuthoring.CreateImporters();

        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.SlangShaderImporter));
        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.GltfSourceImporter));
    }

    [Fact]
    public void AssetRuntimeAssemblyDoesNotReferenceImporterToolchains()
    {
        string[] references = typeof(Texture).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, static name => name.StartsWith("SharpGLTF", StringComparison.Ordinal));
        Assert.DoesNotContain(references, static name => name.StartsWith("SlangShaderSharp", StringComparison.Ordinal));
        Assert.DoesNotContain(references, static name => name.Contains("MeshOptimizer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateProjectImportsAndPublishesOneLooseStorageContract()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "generated.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "GeneratedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.RegisterAssetAsync<Material>(
                "assets/Materials/generated.material.asset");
            IAssetStorage storage = project.CreateStorage();
            Assert.True(storage.TryFind(materialGuid, out AssetEntry entry));
            await using BinaryDocument<Material> document =
                await AssetProject.OpenAsync<Material>(storage, entry);

            Assert.Equal(materialGuid.ToFlatString(), document.Root.AssetGuid);
            Assert.Equal(materialGuid, project.Resolve("assets/Materials/generated.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
