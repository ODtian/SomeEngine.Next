using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
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
        await WriteSimpleShader(dir, "assets/Shaders/simple.shader");

        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            await project.BuildAsync("assets/Shaders/simple.shader");

            ShaderAsset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/simple.shader", "shader:main");
            ShaderAsset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/simple.shader", "shader:main");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            Assert.False(File.Exists(SourceMeta.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.slang"))));
            Assert.Equal(first.Id, AssetText.Describe(File.ReadAllBytes(Path.Combine(dir, "assets", "Shaders", "simple.shader"))).AssetGuid);
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
        await WriteSimpleShader(dir, "assets/Shaders/not_imported.shader");

        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);

            ShaderAsset? loaded = await OpenResolvedShaderAsync(project, "assets/Shaders/not_imported.shader", "shader:main");

            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Shaders/not_imported.shader", "shader:main"));
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
        await WriteSimpleShader(dir, "assets/Shaders/up_to_date.shader");

        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            await project.BuildAsync("assets/Shaders/up_to_date.shader");

            ShaderAsset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/up_to_date.shader", "shader:main");
            Assert.NotNull(first);

            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.CatalogFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            ShaderAsset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/up_to_date.shader", "shader:main");
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
        await WriteSimpleShader(dir, "assets/Shaders/reimport.shader");

        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            await project.BuildAsync("assets/Shaders/reimport.shader");

            ShaderAsset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/reimport.shader", "shader:main");
            Assert.NotNull(first);

            string sourcePath = Path.Combine(dir, "assets", "Shaders", "reimport.slang");
            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.CatalogFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            File.AppendAllText(sourcePath, "\n// force reimport");
            await project.BuildAsync("assets/Shaders/reimport.shader");

            ShaderAsset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/reimport.shader", "shader:main");
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
    public async Task BuildDefinitionPreservesIdentityWhenOutputIsDeleted()
    {
        string dir = CreateTempDir();
        await WriteSimpleShader(dir, "assets/Shaders/deterministic.shader");

        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            await project.BuildAsync("assets/Shaders/deterministic.shader");
            ShaderAsset? first = await OpenResolvedShaderAsync(project, "assets/Shaders/deterministic.shader", "shader:main");
            Assert.NotNull(first);

            string importedPath = Path.Combine(dir, "assets", "Shaders", "deterministic.shader.asset");
            File.Delete(importedPath);
            Assert.Equal(first!.Id, AssetText.Describe(File.ReadAllBytes(Path.Combine(dir, "assets", "Shaders", "deterministic.shader"))).AssetGuid);

            await project.BuildAsync("assets/Shaders/deterministic.shader");
            ShaderAsset? second = await OpenResolvedShaderAsync(project, "assets/Shaders/deterministic.shader", "shader:main");

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
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, materialGuid) {
                Name = "TestMaterial",
            };
            await savedMaterial.SaveAsync(materialPath);

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            using var materialFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/test.material.asset"));
            project.RegisterAsset(materialFile.Describe());
            MaterialAsset? asset = await OpenMaterialAsync(project, materialGuid);

            Assert.NotNull(asset);
            Assert.Equal("TestMaterial", asset!.Name);
            Assert.Equal(materialGuid, asset.Id);
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
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, payloadGuid) {
                Name = "MismatchMaterial",
            };
            await savedMaterial.SaveAsync(fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                manifestGuid,
                "MismatchMaterial",
                path,
                MaterialAsset.FileType,
                MaterialAsset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);

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
            using var shader = new ShaderAsset(SomeEngine.Serialization.Containers.PageArena.Shared, shaderGuid) { Name = "stale" };
            using var code = new ShaderBlock {
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
            };
            shader.SetBlock(code);
            await shader.SaveAsync(shaderAssetPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                shaderGuid,
                "stale",
                "assets/Shaders/stale.shader.asset",
                ShaderAsset.FileType,
                ShaderAsset.SchemaHash,
                sourceGuid,
                "shader:main");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);

            ShaderAsset? root = await OpenShaderAsync(project, shaderGuid);
            Assert.NotNull(root);
            Assert.Empty(root!.GetBlock<ShaderBlock>().EntryPointReflections!);
            await Assert.ThrowsAsync<InvalidDataException>(() => root.GetBlock<ShaderBlock>().CreateAsync(root.Name).AsTask());
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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            const string path = "assets/Materials/created.material.asset";

            using var first = new StandardMaterialAsset
            {
                Name = "CreatedMaterial",
            };
            Guid firstGuid = project.RegisterAsset(await first.SaveAsync(Path.Combine(dir, path)));

            Assert.NotEqual(Guid.Empty, firstGuid);
            Assert.Equal(firstGuid, project.Resolve(path));
            MaterialAsset? firstLoad = await OpenMaterialAsync(project, firstGuid);
            Assert.NotNull(firstLoad);
            Assert.Equal(firstGuid, firstLoad!.Id);
            Assert.Equal("CreatedMaterial", firstLoad.Name);

            using var second = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, firstGuid) {
                Name = "UpdatedMaterial",
            };
            Guid secondGuid = project.RegisterAsset(await second.SaveAsync(Path.Combine(dir, path)));

            Assert.Equal(firstGuid, secondGuid);
            MaterialAsset? secondLoad = await OpenMaterialAsync(project, firstGuid);
            Assert.NotNull(secondLoad);
            Assert.Equal("UpdatedMaterial", secondLoad!.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CreatingAnotherIdentityAtAnExistingPathKeepsTheFileAndDirectoryUnchanged()
    {
        string dir = CreateTempDir();

        try
        {
            const string path = "assets/Materials/reconcile.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            Guid staleGuid = Guid.NewGuid();
            Guid fileGuid = Guid.NewGuid();
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, fileGuid) {
                Name = "ExistingFile",
            };
            await savedMaterial.SaveAsync(fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                staleGuid,
                "Stale",
                path,
                MaterialAsset.FileType,
                MaterialAsset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            using var replacement = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, staleGuid) {
                Name = "Reconciled",
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => replacement.SaveAsync(fullPath).AsTask());
            using var unchanged = await MaterialAsset.OpenAsync(fullPath);
            Assert.Equal(fileGuid, unchanged.Id);
            Assert.Equal("ExistingFile", unchanged.Name);
            Assert.Equal(staleGuid, project.Resolve(path));
            Assert.DoesNotContain(project.List(MaterialAsset.FileType), record => record.AssetGuid == fileGuid);
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
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash);
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", MeshAsset.FileType, MeshAsset.SchemaHash, dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            IReadOnlyList<AssetEntry> materials = project.List(MaterialAsset.FileType);

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
    public async Task Validate_ReportsOrphanMetaAndDanglingReference()
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
            using var shader = new ShaderAsset(SomeEngine.Serialization.Containers.PageArena.Shared, shaderGuid) { Name = "ShaderA" };
            using var code = new ShaderBlock {
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
            };
            shader.SetBlock(code);
            await shader.SaveAsync(shaderAssetPath);

            string materialAssetPath = Path.Combine(dir, "assets", "Materials", "dangling.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialAssetPath)!);
            using var material = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, materialGuid) {
                Name = "DanglingMaterial",
            };
            _ = new StandardMaterialParameters(material.GetBlock<MaterialBlock>()) { AlbedoMap = new(missingTextureGuid) };
            await material.SaveAsync(materialAssetPath);

            // valid source 实际存在
            string validSourceFile = Path.Combine(dir, "assets", "Shaders", "valid.slang");
            File.WriteAllText(validSourceFile, "// shader");

            // 构建 manifest：orphan source 指向不存在的文件，material 依赖不存在的 texture guid
            AssetManifest manifest = new();
            manifest.AddSource(orphanSourceGuid, "assets/Shaders/orphan.slang");
            manifest.AddSource(validSourceGuid, "assets/Shaders/valid.slang");
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/valid.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, validSourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "DanglingMaterial", "assets/Materials/dangling.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [missingTextureGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);

            Guid materialGuid = Guid.NewGuid();
            string materialPath = Path.Combine(dir, "assets", "Materials", "imported.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, materialGuid) {
                Name = "ImportedMaterial",
            };
            await savedMaterial.SaveAsync(materialPath);

            using var materialFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/imported.material.asset"));

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
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, materialGuid) {
                Name = "LazyMaterial",
            };
            await savedMaterial.SaveAsync(materialPath);

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            MaterialAsset? loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy.material.asset");

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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            Assert.Empty(project.List(MaterialAsset.FileType));
            Assert.Empty(project.List(ShaderAsset.FileType));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Project_TryResolveReturnsFalse_WhenNotRegistered()
    {
        string dir = CreateTempDir();
        try
        {
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            MaterialAsset? result = await OpenMaterialAsync(project, Guid.NewGuid());
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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
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
            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
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
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, guid) {
                Name = "Dup",
            };
            await savedMaterial.SaveAsync(path);

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            using var firstFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/dup.material.asset"));
            project.RegisterAsset(firstFile.Describe());
            using var secondFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/dup.material.asset"));
            project.RegisterAsset(secondFile.Describe());

            Assert.Single(project.List(MaterialAsset.FileType));
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

            using var firstMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, g1) { Name = "A" };
            using var secondMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, g2) { Name = "B" };
            await firstMaterial.SaveAsync(p1);
            await secondMaterial.SaveAsync(p2);

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            using var firstFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/a.material.asset"));
            project.RegisterAsset(firstFile.Describe());
            using var secondFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/b.material.asset"));
            project.RegisterAsset(secondFile.Describe());

            Assert.Equal(2, project.List(MaterialAsset.FileType).Count);
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
                MaterialAsset.FileType,
                MaterialAsset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
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
                MaterialAsset.FileType,
                MaterialAsset.SchemaHash);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            Assert.Single(project.List(MaterialAsset.FileType));
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
            using var savedMaterial = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, guid) {
                Name = "Lazy2",
            };
            await savedMaterial.SaveAsync(path);

            AssetProject project = new AssetProject(dir, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat()]);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            MaterialAsset? loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy2.material.asset");
            Assert.Null(loaded);
            Assert.Null(project.Resolve("assets/Materials/lazy2.material.asset"));

            using var materialFile = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, "assets/Materials/lazy2.material.asset"));

            project.RegisterAsset(materialFile.Describe());

            loaded = await OpenResolvedMaterialAsync(project, "assets/Materials/lazy2.material.asset");
            Assert.NotNull(loaded);
            Assert.Equal(guid, project.Resolve("assets/Materials/lazy2.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async ValueTask<ShaderAsset?> OpenResolvedShaderAsync(AssetProject project,
        string path, string? subAssetKey = null)
    {
        Guid? id = project.Resolve(path, subAssetKey);
        return id.HasValue ? await OpenShaderAsync(project, id.Value) : null;
    }

    private static async ValueTask<MaterialAsset?> OpenResolvedMaterialAsync(AssetProject project,
        string path, string? subAssetKey = null)
    {
        Guid? id = project.Resolve(path, subAssetKey);
        return id.HasValue ? await OpenMaterialAsync(project, id.Value) : null;
    }

    private static async ValueTask<ShaderAsset?> OpenShaderAsync(AssetProject project, Guid id)
    {
        IAssetStorage storage = project.CreateStorage();
        if (!storage.TryFind(id, out _)) return null;
        await using var loader = new AssetLoader(storage);
        return await ShaderAsset.OpenAsync(loader, new(id));
    }

    private static async ValueTask<MaterialAsset?> OpenMaterialAsync(AssetProject project, Guid id)
    {
        IAssetStorage storage = project.CreateStorage();
        if (!storage.TryFind(id, out _)) return null;
        await using var loader = new AssetLoader(storage);
        return await MaterialAsset.OpenAsync(loader, new(id));
    }
    private static async Task WriteSimpleShader(string dir, string relativePath)
    {
        string fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string sourcePath = Path.ChangeExtension(fullPath, ".slang");
        File.WriteAllText(sourcePath,
            """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void CSMain()
            {
            }
            """);
        using var definition = new ShaderAsset { Name = Path.GetFileNameWithoutExtension(fullPath) };
        definition.GetBlock<ShaderSourceBlock>().SourceFiles = [Path.GetFileName(sourcePath)];
        await definition.SaveSourceAsync(fullPath);
    }
}
