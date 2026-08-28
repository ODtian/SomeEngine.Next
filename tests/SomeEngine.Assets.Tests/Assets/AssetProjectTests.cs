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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.ImportAsync("assets/Shaders/simple.slang");

            Shader? first = await OpenResolvedAsync<Shader>(project, "assets/Shaders/simple.slang", "shader:main");
            Shader? second = await OpenResolvedAsync<Shader>(project, "assets/Shaders/simple.slang", "shader:main");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
            Assert.True(File.Exists(SourceMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.slang"))));
            Assert.True(File.Exists(AssetMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.shader.asset"))));
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
            AssetProject project = AssetAuthoring.CreateProject(dir);

            Shader? loaded = await OpenResolvedAsync<Shader>(project, "assets/Shaders/not_imported.slang", "shader:main");

            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Shaders/not_imported.slang", "shader:main"));
            Assert.False(File.Exists(SourceMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "not_imported.slang"))));
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.ImportAsync("assets/Shaders/up_to_date.slang");

            Shader? first = await OpenResolvedAsync<Shader>(project, "assets/Shaders/up_to_date.slang", "shader:main");
            Assert.NotNull(first);

            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            Shader? second = await OpenResolvedAsync<Shader>(project, "assets/Shaders/up_to_date.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.ImportAsync("assets/Shaders/reimport.slang");

            Shader? first = await OpenResolvedAsync<Shader>(project, "assets/Shaders/reimport.slang", "shader:main");
            Assert.NotNull(first);

            string sourcePath = Path.Combine(dir, "assets", "Shaders", "reimport.slang");
            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            File.AppendAllText(sourcePath, "\n// force reimport");
            await project.ImportAsync("assets/Shaders/reimport.slang");

            Shader? second = await OpenResolvedAsync<Shader>(project, "assets/Shaders/reimport.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.ImportAsync("assets/Shaders/deterministic.slang");
            Shader? first = await OpenResolvedAsync<Shader>(project, "assets/Shaders/deterministic.slang", "shader:main");
            Assert.NotNull(first);

            string importedPath = Path.Combine(dir, "assets", "Shaders", "deterministic.shader.asset");
            File.Delete(importedPath);
            File.Delete(AssetMetaFiles.GetMetaPath(importedPath));

            await project.ImportAsync("assets/Shaders/deterministic.slang");
            Shader? second = await OpenResolvedAsync<Shader>(project, "assets/Shaders/deterministic.slang", "shader:main");

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
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
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "test.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "TestMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.RegisterAssetAsync<Material>("assets/Materials/test.material.asset");
            Material? asset = await OpenAsync<Material>(project, materialGuid);

            Assert.NotNull(asset);
            Assert.Equal("TestMaterial", asset!.Name);
            Assert.Equal(materialGuid.ToFlatString(), asset.AssetGuid);
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
            AssetGuid manifestGuid = AssetGuid.New();
            AssetGuid payloadGuid = AssetGuid.New();
            const string path = "assets/Materials/mismatch.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = payloadGuid.ToFlatString(),
                Name = "MismatchMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                manifestGuid,
                "MismatchMaterial",
                path,
                AssetType<Material>.Name,
                Material.SchemaFingerprint);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await OpenAsync<Material>(project, manifestGuid));
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
            AssetGuid shaderGuid = AssetGuid.New();
            SourceGuid sourceGuid = SourceGuid.New();
            string sourcePath = Path.Combine(dir, "assets", "Shaders", "stale.slang");
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "stale.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            File.WriteAllText(sourcePath, "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() {}");
            AssetWriter.Write(new Shader
            {
                AssetGuid = shaderGuid.ToFlatString(),
                Name = "stale",
                ImportTrace = new ImportTrace
                {
                    SourceGuid = sourceGuid.ToFlatString(),
                    SourcePath = sourcePath,
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "old",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
                Variants =
                [
                    new ShaderBytecode
                    {
                        Backend = "dxil",
                        Stage = ShaderStage.Compute,
                        EntryPoint = "CSMain",
                        Data = new byte[] { 0x01 },
                        ContentHash = "stale-cs",
                    },
                ],
                EntryPointAttributes = [],
                Reflections = [],
                EntryPointReflections = [],
                Metadata = new ShaderMetadata
                {
                    Tags = [],
                    MaterialBindings = [],
                    MaterialScalarLayouts = [],
                },
            }, shaderAssetPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                shaderGuid,
                "stale",
                "assets/Shaders/stale.shader.asset",
                AssetType<Shader>.Name,
                Shader.SchemaFingerprint,
                sourceGuid,
                "shader:main");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);

            Shader? root = await OpenAsync<Shader>(project, shaderGuid);
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            const string path = "assets/Materials/created.material.asset";

            AssetGuid firstGuid = project.CreateAsset(path, new Material
            {
                Name = "CreatedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.False(firstGuid.IsEmpty);
            Assert.Equal(firstGuid, project.Resolve(path));
            Material? firstLoad = await OpenAsync<Material>(project, firstGuid);
            Assert.NotNull(firstLoad);
            Assert.Equal(firstGuid.ToFlatString(), firstLoad!.AssetGuid);
            Assert.Equal("CreatedMaterial", firstLoad.Name);

            AssetGuid secondGuid = project.CreateAsset(path, new Material
            {
                Name = "UpdatedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.Equal(firstGuid, secondGuid);
            Material? secondLoad = await OpenAsync<Material>(project, firstGuid);
            Assert.NotNull(secondLoad);
            Assert.Equal("UpdatedMaterial", secondLoad!.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CreateAsset_UsesRegisteredGuid_WhenExistingFileGuidDiffers()
    {
        string dir = CreateTempDir();

        try
        {
            const string path = "assets/Materials/reconcile.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            AssetGuid staleGuid = AssetGuid.New();
            AssetGuid fileGuid = AssetGuid.New();
            AssetWriter.Write(new Material
            {
                AssetGuid = fileGuid.ToFlatString(),
                Name = "ExistingFile",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                staleGuid,
                "Stale",
                path,
                AssetType<Material>.Name,
                Material.SchemaFingerprint);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
            AssetGuid createdGuid = project.CreateAsset(path, new Material
            {
                Name = "Reconciled",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.Equal(staleGuid, createdGuid);
            Assert.Equal(staleGuid, project.Resolve(path));
            Assert.DoesNotContain(project.List(AssetType<Material>.Name), record => record.Guid == fileGuid);
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
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();
            AssetGuid meshGuid = AssetGuid.New();

            AssetManifest manifest = new();
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint);
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", AssetType<Mesh>.Name, Mesh.SchemaFingerprint, dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
            IReadOnlyList<AssetManifestRecord> materials = project.List(AssetType<Material>.Name);

            Assert.Single(materials);
            Assert.Equal(materialGuid, materials[0].Guid);
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
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid missingShaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();

            // 创建 valid shader .asset 文件和 material .asset 文件
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "valid.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            AssetWriter.Write(new Shader
            {
                AssetGuid = shaderGuid.ToFlatString(),
                Name = "ShaderA",
                Metadata = new ShaderMetadata { Tags = [], MaterialBindings = [] },
                Variants = [],
                Reflections = [],
                ImportTrace = new ImportTrace
                {
                    SourceGuid = validSourceGuid.ToFlatString(),
                    SourcePath = "assets/Shaders/valid.slang",
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "fp",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
            }, shaderAssetPath);

            string materialAssetPath = Path.Combine(dir, "assets", "Materials", "dangling.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialAssetPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "DanglingMaterial",
                Passes =
                [
                    new PassEntry
                    {
                        Shader = new ShaderRef
                        {
                            AssetGuid = missingShaderGuid.ToFlatString(),
                            EntryPoint = "main",
                            Stage = ShaderStage.Compute,
                        },
                    },
                ],
                Textures = [],
                Scalars = [],
            }, materialAssetPath);

            // valid source 实际存在
            string validSourceFile = Path.Combine(dir, "assets", "Shaders", "valid.slang");
            File.WriteAllText(validSourceFile, "// shader");

            // 构建 manifest：orphan source 指向不存在的文件，material 依赖不存在的 shader guid
            AssetManifest manifest = new();
            manifest.AddSource(orphanSourceGuid, "assets/Shaders/orphan.slang");
            manifest.AddSource(validSourceGuid, "assets/Shaders/valid.slang");
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/valid.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, validSourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "DanglingMaterial", "assets/Materials/dangling.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [missingShaderGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
            IReadOnlyList<AssetDiagnostic> diagnostics = project.Validate();

            Assert.Contains(diagnostics, static diagnostic => diagnostic.Kind == AssetDiagnosticKind.OrphanSourceMeta);
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == AssetDiagnosticKind.DanglingReference && diagnostic.AssetGuid == materialGuid && diagnostic.RelatedAssetGuid == missingShaderGuid);
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
            AssetProject project = AssetAuthoring.CreateProject(dir);

            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "imported.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "ImportedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetGuid imported = await project.RegisterAssetAsync<Material>(
                "assets/Materials/imported.material.asset");

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
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "lazy.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "LazyMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            Material? loaded = await OpenResolvedAsync<Material>(project, "assets/Materials/lazy.material.asset");

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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            Assert.Empty(project.List(AssetType<Material>.Name));
            Assert.Empty(project.List(AssetType<Shader>.Name));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Project_TryResolveReturnsFalse_WhenNotRegistered()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = AssetAuthoring.CreateProject(dir);
            Material? result = await OpenAsync<Material>(project, AssetGuid.New());
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
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
            AssetProject project = AssetAuthoring.CreateProject(dir);
            Assert.Empty(project.GetDependencies(AssetGuid.New()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetReferencers_ReturnsEmpty_ForUnknownGuid()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = AssetAuthoring.CreateProject(dir);
            Assert.Empty(project.GetReferencers(AssetGuid.New()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_Idempotent_DoesNotDuplicate()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            string path = Path.Combine(dir, "assets", "Materials", "dup.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Dup",
                Passes = [], Textures = [], Scalars = [],
            }, path);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.RegisterAssetAsync<Material>("assets/Materials/dup.material.asset");
            await project.RegisterAssetAsync<Material>("assets/Materials/dup.material.asset");

            Assert.Single(project.List(AssetType<Material>.Name));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_MultipleAssets_AllResolvable()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid g1 = AssetGuid.New(), g2 = AssetGuid.New();
            string p1 = Path.Combine(dir, "assets", "Materials", "a.material.asset");
            string p2 = Path.Combine(dir, "assets", "Materials", "b.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(p1)!);

            AssetWriter.Write(new Material { AssetGuid = g1.ToFlatString(), Name = "A", Passes = [], Textures = [], Scalars = [] }, p1);
            AssetWriter.Write(new Material { AssetGuid = g2.ToFlatString(), Name = "B", Passes = [], Textures = [], Scalars = [] }, p2);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            await project.RegisterAssetAsync<Material>("assets/Materials/a.material.asset");
            await project.RegisterAssetAsync<Material>("assets/Materials/b.material.asset");

            Assert.Equal(2, project.List(AssetType<Material>.Name).Count);
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
            AssetGuid guid = AssetGuid.New();
            AssetManifest manifest = new();
            manifest.AddAsset(
                guid,
                "Ghost",
                "assets/Materials/ghost.material.asset",
                AssetType<Material>.Name,
                Material.SchemaFingerprint);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
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
            AssetGuid guid = AssetGuid.New();
            AssetManifest manifest = new();
            manifest.AddAsset(
                guid,
                "Pre",
                "assets/Materials/pre.material.asset",
                AssetType<Material>.Name,
                Material.SchemaFingerprint);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = AssetAuthoring.CreateProject(dir);
            Assert.Single(project.List(AssetType<Material>.Name));
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
            AssetGuid guid = AssetGuid.New();
            string path = Path.Combine(dir, "assets", "Materials", "lazy2.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AssetWriter.Write(new Material
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Lazy2",
                Passes = [], Textures = [], Scalars = [],
            }, path);

            AssetProject project = AssetAuthoring.CreateProject(dir);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            Material? loaded = await OpenResolvedAsync<Material>(project, "assets/Materials/lazy2.material.asset");
            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            await project.RegisterAssetAsync<Material>("assets/Materials/lazy2.material.asset");

            loaded = await OpenResolvedAsync<Material>(project, "assets/Materials/lazy2.material.asset");
            Assert.NotNull(loaded);
            Assert.Equal(guid, project.Resolve("assets/Materials/lazy2.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async ValueTask<TContract?> OpenResolvedAsync<TContract>(
        AssetProject project,
        string sourcePath,
        string? subAssetKey = null)
        where TContract : class, IBinaryContract<TContract>
    {
        AssetGuid? guid = project.Resolve(sourcePath, subAssetKey);
        return guid.HasValue ? await OpenAsync<TContract>(project, guid.Value) : null;
    }

    private static async ValueTask<TContract?> OpenAsync<TContract>(
        AssetProject project,
        AssetGuid guid)
        where TContract : class, IBinaryContract<TContract>
    {
        IAssetStorage storage = project.CreateStorage();
        if (!storage.TryFind(guid, out AssetEntry entry))
            return null;

        await using BinaryDocument<TContract> document =
            await AssetProject.OpenAsync<TContract>(storage, entry);
        return document.Root;
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
