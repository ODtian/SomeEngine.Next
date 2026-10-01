using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetStorageTests
{
    [Fact]
    public void CreateImporters_ExposesAuthoringPipelineTypes()
    {
        IReadOnlyList<SomeEngine.Assets.Importing.IAssetSourceImporter> importers = [Game.Importing.StandardGltfSceneImporter.CreateSourceImporter()];
        IReadOnlyList<IAssetSourceFormat> formats = [new ShaderSourceFormat()];

        Assert.Contains(formats, format => format.SourceExtension == ".shader");
        Assert.Contains(importers, importer => importer.SourceExtensions.Contains(".gltf") && importer.SourceExtensions.Contains(".glb"));
    }

    [Fact]
    public void AssetRuntimeAssemblyDoesNotReferenceImporterToolchains()
    {
        string[] references = typeof(TextureAsset).Assembly
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
            using var generated = new StandardMaterialAsset(materialGuid) {
                Name = "GeneratedMaterial",
            };
            await generated.SaveAsync(materialPath);

            AssetProject project = new AssetProject(dir, [Game.Importing.StandardGltfSceneImporter.CreateSourceImporter()], sourceFormats: [new ShaderSourceFormat()]);
            using var materialFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/generated.material.asset"));
            project.RegisterAsset(materialFile.Describe());
            IAssetStorage storage = project.CreateStorage();
            Assert.True(storage.TryFind(materialGuid, out AssetEntry entry));
            await using var loader = new AssetLoader(storage);
            using MaterialAsset file = await MaterialAsset.OpenAsync(loader, new(entry.AssetGuid));

            Assert.Equal(materialGuid, file.Id);
            Assert.Equal(materialGuid, project.Resolve("assets/Materials/generated.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
