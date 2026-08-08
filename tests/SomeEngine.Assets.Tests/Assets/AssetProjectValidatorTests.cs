using System;
using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
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
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();
            AssetGuid meshGuid = AssetGuid.New();

            // 创建实际文件以通过 MissingAssetFile 检查
            CreateDummyFile(dir, "assets/Shaders/a.shader.asset");
            CreateDummyFile(dir, "assets/Materials/a.material.asset");
            CreateDummyFile(dir, "assets/Meshes/a.mesh.asset");

            AssetManifest manifest = new();
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint);
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", AssetType<Mesh>.Name, Mesh.SchemaFingerprint, dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
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
            AssetGuid mainGuid = AssetGuid.New();
            AssetGuid shadowGuid = AssetGuid.New();

            AssetManifest manifest = new();
            manifest.AddSource(sourceGuid, "assets/Shaders/shared.slang");
            manifest.AddAsset(mainGuid, "Main", "assets/Shaders/shared.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, sourceGuid, "shader:main");
            manifest.AddAsset(shadowGuid, "Shadow", "assets/Shaders/shared.shadow.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, sourceGuid, "shader:shadow");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);

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
