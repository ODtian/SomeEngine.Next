using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public sealed class AssetProjectTests
{
    [Fact]
    public async Task Import_SourceShader_ThenProjectOpen_ReusesGuid()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/simple.slang");

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            await project.ImportAsync("assets/Shaders/simple.slang");

            Shader.Asset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/simple.slang", "shader:main");
            Shader.Asset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/simple.slang", "shader:main");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            Assert.True(File.Exists(SourceMeta.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.slang"))));
            Assert.NotNull(SourceMeta.Load(Path.Combine(dir, "assets", "Shaders", "simple.slang")).FindOutput(Path.Combine(dir, "assets", "Shaders", "simple.shader.asset")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Resolve_UnregisteredSource_DoesNotImport()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/not_imported.slang");

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);

            Shader.Asset? loaded = await OpenResolvedShaderAsync(project, "assets/Shaders/not_imported.slang", "shader:main");

            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Shaders/not_imported.slang", "shader:main"));
            Assert.False(File.Exists(SourceMeta.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "not_imported.slang"))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ProjectOpen_DoesNotReimport_WhenSourceIsUpToDate()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/up_to_date.slang");

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            await project.ImportAsync("assets/Shaders/up_to_date.slang");

            Shader.Asset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/up_to_date.slang", "shader:main");
            Assert.NotNull(first);

            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            Shader.Asset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/up_to_date.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_BySourcePath_Reimports_WhenSourceChanges()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/reimport.slang");

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            await project.ImportAsync("assets/Shaders/reimport.slang");

            Shader.Asset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/reimport.slang", "shader:main");
            Assert.NotNull(first);

            string sourcePath = Path.Combine(dir, "assets", "Shaders", "reimport.slang");
            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            File.AppendAllText(sourcePath, "\n// force reimport");
            await project.ImportAsync("assets/Shaders/reimport.slang");

            Shader.Asset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/reimport.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            Assert.True(after > before);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_BySourcePath_PreservesDeterministicShaderGuid_WhenImportedMetaIsMissing()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/deterministic.slang");

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            await project.ImportAsync("assets/Shaders/deterministic.slang");
            Shader.Asset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/deterministic.slang", "shader:main");
            Assert.NotNull(first);

            string importedPath = Path.Combine(dir, "assets", "Shaders", "deterministic.shader.asset");
            File.Delete(importedPath);
            Assert.NotNull(SourceMeta.Load(Path.Combine(dir, "assets", "Shaders", "deterministic.slang")).FindOutput(importedPath));

            await project.ImportAsync("assets/Shaders/deterministic.slang");
            Shader.Asset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/deterministic.slang", "shader:main");

            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ProjectOpen_ByGuid_ReturnsMaterialAfterImport()
    {
        string dir = CreateTempDir();

        try
        {
            Guid materialGuid = Guid.NewGuid();
            string materialPath = Path.Combine(dir, "assets", "Materials", "test.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
                Name = "TestMaterial",
            })).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            using var materialFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/test.material.asset"));
            project.RegisterAsset(materialFile.Describe());
            Material.Asset? asset = await OpenMaterialAsync(project, materialGuid);

            Assert.NotNull(asset);
            Assert.Equal("TestMaterial", asset!.Name);
            Assert.Equal(materialGuid, asset.Id.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ProjectOpen_ByGuid_RejectsPayloadGuidMismatch()
    {
        string dir = CreateTempDir();

        try
        {
            Guid manifestGuid = Guid.NewGuid();
            Guid payloadGuid = Guid.NewGuid();
            const string path = "assets/Materials/mismatch.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(payloadGuid)) {
                Name = "MismatchMaterial",
            })).SaveAsync(fullPath).AsTask().GetAwaiter().GetResult();

            AssetManifest manifest = new();
            manifest.AddAsset(
                manifestGuid,
                "MismatchMaterial",
                path,
                Material.Asset.FileType,
                Material.Asset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await OpenMaterialAsync(project, manifestGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Project_OpensShaderDataWithoutApplyingRuntimeProjectionRules()
    {
        string dir = CreateTempDir();

        try
        {
            Guid shaderGuid = Guid.NewGuid();
            SourceGuid sourceGuid = SourceGuid.New();
            string sourcePath = Path.Combine(dir, "assets", "Shaders", "stale.slang");
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "stale.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            File.WriteAllText(sourcePath, "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() {}");
            ((new Shader.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Shader>(shaderGuid)) {
                Name = "stale",
                ImportTrace = new ImportTrace
                {
                    SourceGuid = sourceGuid.ToString(),
                    SourcePath = sourcePath,
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "old",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
                Programs = [],
                EntryPointAttributes = [],
                Reflections = [],
                EntryPointReflections = [],
                Metadata = new ShaderMetadata
                {
                    Tags = [],
                    MaterialBindings = [],
                    MaterialScalarLayouts = [],
                },
            })).SaveAsync(shaderAssetPath).AsTask().GetAwaiter().GetResult();

            AssetManifest manifest = new();
            manifest.AddAsset(
                shaderGuid,
                "stale",
                "assets/Shaders/stale.shader.asset",
                Shader.Asset.FileType,
                Shader.Asset.SchemaHash,
                sourceGuid,
                "shader:main");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);

            Shader.Asset? root = await OpenShaderAsync(project, shaderGuid);
            Assert.NotNull(root);
            Assert.Empty(root!.EntryPointReflections!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CreateAsset_AssignsGuid_RegistersManifest_AndReusesExistingGuid()
    {
        string dir = CreateTempDir();

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            const string path = "assets/Materials/created.material.asset";

            using var first = new StandardMaterial.Asset
            {
                Name = "CreatedMaterial",
            };
            Guid firstGuid = project.RegisterAsset(await first.SaveAsync(Path.Combine(dir, path)));

            Assert.NotEqual(Guid.Empty, firstGuid);
            Assert.Equal(firstGuid, project.Resolve(path));
            Material.Asset? firstLoad = await OpenMaterialAsync(project, firstGuid);
            Assert.NotNull(firstLoad);
            Assert.Equal(firstGuid, firstLoad!.Id.Value);
            Assert.Equal("CreatedMaterial", firstLoad.Name);

            using var second = new StandardMaterial.Asset(new(firstGuid)) {
                Name = "UpdatedMaterial",
            };
            Guid secondGuid = project.RegisterAsset(await second.SaveAsync(Path.Combine(dir, path)));

            Assert.Equal(firstGuid, secondGuid);
            Material.Asset? secondLoad = await OpenMaterialAsync(project, firstGuid);
            Assert.NotNull(secondLoad);
            Assert.Equal("UpdatedMaterial", secondLoad!.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CreateAsset_UsesRegisteredGuid_WhenExistingFileGuidDiffers()
    {
        string dir = CreateTempDir();

        try
        {
            const string path = "assets/Materials/reconcile.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            Guid staleGuid = Guid.NewGuid();
            Guid fileGuid = Guid.NewGuid();
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(fileGuid)) {
                Name = "ExistingFile",
            })).SaveAsync(fullPath).AsTask().GetAwaiter().GetResult();

            AssetManifest manifest = new();
            manifest.AddAsset(
                staleGuid,
                "Stale",
                path,
                Material.Asset.FileType,
                Material.Asset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            using var replacement = new StandardMaterial.Asset(new(staleGuid)) {
                Name = "Reconciled",
            };
            Guid createdGuid = project.RegisterAsset(await replacement.SaveAsync(fullPath));

            Assert.Equal(staleGuid, createdGuid);
            Assert.Equal(staleGuid, project.Resolve(path));
            Assert.DoesNotContain(project.List(Material.Asset.FileType), record => record.AssetGuid == fileGuid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void List_Filter_And_DependencyQueries_Work()
    {
        string dir = CreateTempDir();

        try
        {
            Guid shaderGuid = Guid.NewGuid();
            Guid materialGuid = Guid.NewGuid();
            Guid meshGuid = Guid.NewGuid();

            AssetManifest manifest = new();
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash);
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", Material.Asset.FileType, Material.Asset.SchemaHash, dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", Mesh.Asset.FileType, Mesh.Asset.SchemaHash, dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            IReadOnlyList<AssetEntry> materials = project.List(Material.Asset.FileType);

            Assert.Single(materials);
            Assert.Equal(materialGuid, materials[0].AssetGuid);
            Assert.Equal(new[] { shaderGuid }, project.GetDependencies(materialGuid));
            Assert.Equal(new[] { meshGuid }, project.GetReferencers(materialGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Validate_ReportsOrphanMetaAndDanglingReference()
    {
        string dir = CreateTempDir();

        try
        {
            SourceGuid orphanSourceGuid = SourceGuid.New();
            SourceGuid validSourceGuid = SourceGuid.New();
            Guid shaderGuid = Guid.NewGuid();
            Guid missingTextureGuid = Guid.NewGuid();
            Guid materialGuid = Guid.NewGuid();

            // 创建 valid shader .asset 文件和 material .asset 文件
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "valid.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            ((new Shader.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Shader>(shaderGuid)) {
                Name = "ShaderA",
                Metadata = new ShaderMetadata { Tags = [], MaterialBindings = [] },
                Programs = [],
                Reflections = [],
                ImportTrace = new ImportTrace
                {
                    SourceGuid = validSourceGuid.ToString(),
                    SourcePath = "assets/Shaders/valid.slang",
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "fp",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
            })).SaveAsync(shaderAssetPath).AsTask().GetAwaiter().GetResult();

            string materialAssetPath = Path.Combine(dir, "assets", "Materials", "dangling.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialAssetPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
                Name = "DanglingMaterial",
                AlbedoMap = new(missingTextureGuid),
            })).SaveAsync(materialAssetPath).AsTask().GetAwaiter().GetResult();

            // valid source 实际存在
            string validSourceFile = Path.Combine(dir, "assets", "Shaders", "valid.slang");
            File.WriteAllText(validSourceFile, "// shader");

            // 构建 manifest：orphan source 指向不存在的文件，material 依赖不存在的 texture guid
            AssetManifest manifest = new();
            manifest.AddSource(orphanSourceGuid, "assets/Shaders/orphan.slang");
            manifest.AddSource(validSourceGuid, "assets/Shaders/valid.slang");
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/valid.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash, validSourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "DanglingMaterial", "assets/Materials/dangling.material.asset", Material.Asset.FileType, Material.Asset.SchemaHash, dependencies: [missingTextureGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            IReadOnlyList<AssetDiagnostic> diagnostics = project.Validate();

            Assert.Contains(diagnostics, static diagnostic => diagnostic.Kind == AssetDiagnosticKind.OrphanSourceMeta);
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == AssetDiagnosticKind.DanglingReference && diagnostic.AssetGuid == materialGuid && diagnostic.RelatedAssetGuid == missingTextureGuid);
            Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Kind == AssetDiagnosticKind.MissingAssetFile);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task Import_AssetFile_RebuildsIndexAndResolveWorks()
    {
        string dir = CreateTempDir();

        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);

            Guid materialGuid = Guid.NewGuid();
            string materialPath = Path.Combine(dir, "assets", "Materials", "imported.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
                Name = "ImportedMaterial",
            })).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();

            using var materialFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/imported.material.asset"));

            Guid imported = project.RegisterAsset(materialFile.Describe());

            Assert.Equal(materialGuid, imported);
            Assert.Equal(materialGuid, project.Resolve("assets/Materials/imported.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LooseStorageDoesNotDiscoverUnregisteredAssetFiles()
    {
        string dir = CreateTempDir();

        try
        {
            Guid materialGuid = Guid.NewGuid();
            string materialPath = Path.Combine(dir, "assets", "Materials", "lazy.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
                Name = "LazyMaterial",
            })).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Material.Asset? loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy.material.asset");

            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Materials/lazy.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
    [Fact]
    public void Constructor_WithNoManifest_StartsEmpty()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Empty(project.List(Material.Asset.FileType));
            Assert.Empty(project.List(Shader.Asset.FileType));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Project_TryResolveReturnsFalse_WhenNotRegistered()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Material.Asset? result = await OpenMaterialAsync(project, Guid.NewGuid());
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnknownPath()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Null(project.Resolve("assets/Materials/nonexistent.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetDependencies_ReturnsEmpty_ForUnknownGuid()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Empty(project.GetDependencies(Guid.NewGuid()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetReferencers_ReturnsEmpty_ForUnknownGuid()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Empty(project.GetReferencers(Guid.NewGuid()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_Idempotent_DoesNotDuplicate()
    {
        string dir = CreateTempDir();
        try
        {
            Guid guid = Guid.NewGuid();
            string path = Path.Combine(dir, "assets", "Materials", "dup.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(guid)) {
                Name = "Dup",
            })).SaveAsync(path).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            using var firstFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/dup.material.asset"));
            project.RegisterAsset(firstFile.Describe());
            using var secondFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/dup.material.asset"));
            project.RegisterAsset(secondFile.Describe());

            Assert.Single(project.List(Material.Asset.FileType));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_MultipleAssets_AllResolvable()
    {
        string dir = CreateTempDir();
        try
        {
            Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid();
            string p1 = Path.Combine(dir, "assets", "Materials", "a.material.asset");
            string p2 = Path.Combine(dir, "assets", "Materials", "b.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(p1)!);

            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(g1)) { Name = "A" })).SaveAsync(p1).AsTask().GetAwaiter().GetResult();
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(g2)) { Name = "B" })).SaveAsync(p2).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            using var firstFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/a.material.asset"));
            project.RegisterAsset(firstFile.Describe());
            using var secondFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/b.material.asset"));
            project.RegisterAsset(secondFile.Describe());

            Assert.Equal(2, project.List(Material.Asset.FileType).Count);
            Assert.Equal(g1, project.Resolve("assets/Materials/a.material.asset"));
            Assert.Equal(g2, project.Resolve("assets/Materials/b.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Validate_Reports_MissingAssetFile()
    {
        string dir = CreateTempDir();
        try
        {
            Guid guid = Guid.NewGuid();
            AssetManifest manifest = new();
            manifest.AddAsset(
                guid,
                "Ghost",
                "assets/Materials/ghost.material.asset",
                Material.Asset.FileType,
                Material.Asset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            IReadOnlyList<AssetDiagnostic> diagnostics = project.Validate();

            Assert.Contains(diagnostics, d => d.Kind == AssetDiagnosticKind.MissingAssetFile && d.AssetGuid == guid);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Constructor_LoadsExistingManifest()
    {
        string dir = CreateTempDir();
        try
        {
            Guid guid = Guid.NewGuid();
            AssetManifest manifest = new();
            manifest.AddAsset(
                guid,
                "Pre",
                "assets/Materials/pre.material.asset",
                Material.Asset.FileType,
                Material.Asset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Single(project.List(Material.Asset.FileType));
            Assert.Equal(guid, project.Resolve("assets/Materials/pre.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ProjectOpen_ByPath_RequiresManifestRegistration()
    {
        string dir = CreateTempDir();
        try
        {
            Guid guid = Guid.NewGuid();
            string path = Path.Combine(dir, "assets", "Materials", "lazy2.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(guid)) {
                Name = "Lazy2",
            })).SaveAsync(path).AsTask().GetAwaiter().GetResult();

            AssetProject project = new AssetProject(dir, [new GltfSourceImporter(), new SlangSourceImporter()]);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            Material.Asset? loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy2.material.asset");
            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            using var materialFile = await Material.Asset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/lazy2.material.asset"));

            project.RegisterAsset(materialFile.Describe());

            loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy2.material.asset");
            Assert.NotNull(loaded);
            Assert.Equal(guid, project.Resolve("assets/Materials/lazy2.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async ValueTask<Shader.Asset?> OpenResolvedShaderAsync(AssetProject project,
        string path, string? subAssetKey = null)
    {
        Guid? id = project.Resolve(path, subAssetKey);
        return id.HasValue ? await OpenShaderAsync(project, id.Value) : null;
    }

    private static async ValueTask<Material.Asset?> OpenResolvedMaterialAsync(AssetProject project,
        string path, string? subAssetKey = null)
    {
        Guid? id = project.Resolve(path, subAssetKey);
        return id.HasValue ? await OpenMaterialAsync(project, id.Value) : null;
    }

    private static async ValueTask<Shader.Asset?> OpenShaderAsync(AssetProject project, Guid id)
    {
        IAssetStorage storage = project.CreateStorage();
        if (!storage.TryFind(id, out _)) return null;
        await using var loader = new AssetLoader(storage);
        return await Shader.Asset.OpenAsync(loader, new(id));
    }

    private static async ValueTask<Material.Asset?> OpenMaterialAsync(AssetProject project, Guid id)
    {
        IAssetStorage storage = project.CreateStorage();
        if (!storage.TryFind(id, out _)) return null;
        await using var loader = new AssetLoader(storage);
        return await Material.Asset.OpenAsync(loader, new(id));
    }
    private static void WriteSimpleShader(string dir, string relativePath)
    {
        string fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath,
            """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void CSMain()
            {
            }
            """);
    }
}
