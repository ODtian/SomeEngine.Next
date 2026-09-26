using System;
using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetProjectValidatorTests
{
    [Fact]
    public void Validate_DoesNotReportReferencedAssetsAsOrphan()
    {
        string dir = CreateTempDir();

        try
        {
            Guid shaderGuid = Guid.NewGuid();
            Guid materialGuid = Guid.NewGuid();
            Guid meshGuid = Guid.NewGuid();

            // 创建实际文件以通过 MissingAssetFile 检查
            CreateDummyFile(dir, "assets/Shaders/a.shader.asset");
            CreateDummyFile(dir, "assets/Materials/a.material.asset");
            CreateDummyFile(dir, "assets/Meshes/a.mesh.asset");

            AssetManifest manifest = new();
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash);
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", MeshAsset.FileType, MeshAsset.SchemaHash, dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, []);
            IReadOnlyList<AssetDiagnostic> diagnostics = project.Validate();

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Kind == AssetDiagnosticKind.MissingAssetFile);
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Kind == AssetDiagnosticKind.DanglingReference);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resolve_RequiresSubAssetKey_WhenSourceHasMultipleOutputs()
    {
        string dir = CreateTempDir();

        try
        {
            SourceGuid sourceGuid = SourceGuid.New();
            Guid mainGuid = Guid.NewGuid();
            Guid shadowGuid = Guid.NewGuid();

            AssetManifest manifest = new();
            manifest.AddSource(sourceGuid, "assets/Shaders/shared.slang");
            manifest.AddAsset(mainGuid, "Main", "assets/Shaders/shared.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, sourceGuid, "shader:main");
            manifest.AddAsset(shadowGuid, "Shadow", "assets/Shaders/shared.shadow.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, sourceGuid, "shader:shadow");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, []);

            Assert.Null(project.Resolve("assets/Shaders/shared.slang"));
            Assert.Equal(mainGuid, project.Resolve("assets/Shaders/shared.slang", "shader:main"));
            Assert.Equal(shadowGuid, project.Resolve("assets/Shaders/shared.slang", "shader:shadow"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void CreateDummyFile(string dir, string relativePath)
    {
        string fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "{}");
    }
}
